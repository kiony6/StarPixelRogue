#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""《星界像素狂潮》离线平衡模拟器（M3.6 建立，M8 扩展至 9 武器 + 双角色）。

把 Unity 原型的核心机制忠实镜像为可执行模型，用于在无 Unity 环境的情况下量化验证数值曲线：
  - 刷怪：波次数量 / 边缘生成 / 生成间隔随波次加快 / 难度递增
  - 敌人：三类 AI（追击/疾行/重甲）+ 精英怪 + 接触伤害
  - 玩家：16 方向势场走位、9 种武器 × 4 级、弹道飞行、AoE、环绕刃、
    跳电（雷霆之链）、回旋镖（月刃）、追踪导弹（蜂群导弹）、持续光环（圣光领域）
  - 角色：星尘游侠（经验+15%）/ 重装守卫（生命/伤害/范围换移速），乘区初始化时应用
  - 成长：经验曲线、升级 3 选 1 抽卡（武器解锁 / 武器升级 / 属性强化）、组合技
    （规则顺序赋值、后写覆盖——与 C# ComboManager.Combos 数组顺序一致）
  - 生存：回血球掉落与磁吸拾取
  - Boss：每 5 波，径向弹幕 + 召唤（P1 参数；阶段递进未镜像，见 README 已知限制）

输出：分角色存活波次分布、关键指标、武器采用率、角色对比、曲线失衡告警。
所有常量与 C# 侧一一对应，改这里先试算，验证通过后再同步回工程。
"""
import random
import math
import statistics

# ---------------- 全局配置（镜像 C# 常量） ----------------
CFG = dict(
    dt=0.05,
    spawn_radius=9.0,
    spawn_interval=0.15,
    spawn_interval_min=0.07,
    spawn_interval_dec=0.0025,
    contact_dist=0.7,
    contact_cd=0.5,
    player_hp=110.0,
    player_speed=5.6,
    pickup_radius=1.6,
    xp_base=5.0,
    xp_growth=1.25,
    xp_add=2.0,
    diff_per_wave=0.10,
    # M8 调参（方案 B）：Wave 30 起难度递增 0.10→0.18/波，补偿 XP 停滞后的 build 成长断崖
    diff_knee_wave=30,
    diff_per_wave_late=0.18,
    # M8 调参（方案 B）：疾行怪速度封顶，保留风筝可能（Wave 23 起原公式超玩家移速 5.6）
    runner_speed_cap=5.2,
    wave_count_base=8,
    wave_count_per=4,
    boss_every=5,
    boss_hp_base=400.0,
    boss_hp_per=90.0,
    elite_from_wave=3,
    elite_chance_base=0.06,
    elite_chance_per_wave=0.01,
    elite_chance_max=0.26,
    elite_hp_mult=3.2,
    elite_score_mult=5,
    health_drop_chance=0.05,
    health_drop_amount=15.0,
    elite_health_amount=30.0,
    boss_health_amount=40.0,
    weapon_max_level=4,
    proj_hit_radius=0.35 + 0.40,
    boss_hit_radius=0.35 + 0.40 + 1.0,
    max_time=900.0,
)

KINDS = {
    'chaser': dict(hp=20.0, speed=2.2, speed_per_wave=0.06, contact=6.0, score=1),
    'runner': dict(hp=12.0, speed=3.8, speed_per_wave=0.08, contact=9.0, score=1,
                   speed_cap=5.2),   # M8 调参：Wave 23 起原公式超玩家移速 5.6，封顶保留风筝
    'tank':   dict(hp=60.0, speed=1.4, speed_per_wave=0.05, contact=11.0, score=3),
}


def wave_diff(wave):
    """难度系数：Wave<=knee 线性 diff_per_wave；超过后改走 diff_per_wave_late（M8 方案 B）。
    knee<=0 视为未启用分段，全程单斜率。"""
    knee = CFG.get('diff_knee_wave', 0)
    if knee and wave > knee:
        return (1.0 + (knee - 1) * CFG['diff_per_wave']
                + (wave - knee) * CFG.get('diff_per_wave_late', CFG['diff_per_wave']))
    return 1.0 + (wave - 1) * CFG['diff_per_wave']

# 武器基础表（Lv1）。升级效果见 weapon_stats()
# M8：补齐 M6.1 四种新武器（雷霆之链/月刃/蜂群导弹/圣光领域），数值与 C# UpgradeCatalog/SkillDef 一一对应
WEAPONS = {
    1: dict(name='星能弹', type='projectile', dmg=18.0, cd=0.35, speed=9.0,
            pierce=0, count=1, rng=7.0, tags=('fire',)),
    2: dict(name='散射枪', type='scatter', dmg=10.0, cd=0.55, speed=9.0,
            pierce=0, count=3, rng=7.0, tags=('storm',)),
    3: dict(name='寒霜新星', type='nova', dmg=22.0, cd=1.80, radius=3.0, tags=('ice',)),
    4: dict(name='裂地波', type='nova', dmg=32.0, cd=2.60, radius=4.5, tags=('earth',)),
    5: dict(name='雷环刃', type='orbit', dmg=12.0, cd=0.0, radius=2.0,
            count=1, hit_cd=0.30, tags=('lightning',)),
    # —— M6.1 新武器族 ——
    6: dict(name='雷霆之链', type='chain', dmg=26.0, cd=1.60, rng=7.0,
            jumps=3, chain_range=4.0, falloff=0.8, tags=('lightning',)),
    7: dict(name='月刃', type='boomerang', dmg=20.0, cd=1.40, speed=10.0,
            count=1, rng=7.0, max_range=6.0, tags=('storm',)),
    8: dict(name='蜂群导弹', type='homing', dmg=12.0, cd=1.80, speed=7.0,
            count=2, rng=9.0, turn=8.0, tags=('storm',)),
    9: dict(name='圣光领域', type='aura', dmg=10.0, cd=0.0, radius=3.2,
            interval=0.5, tags=('holy',)),
    # M9 第10把武器：脉冲地雷（区域封锁；earth 标签与寒霜新星构成 ice+earth 减速）
    10: dict(name='脉冲地雷', type='mine', dmg=45.0, cd=2.0, radius=2.6,
            count=1, arm=0.35, trigger=1.6, life=8.0, tags=('earth',)),
}

# 可解锁武器池（镜像 UpgradeCatalog.UnlockableWeaponIds：起始武器不在池内）
UNLOCKABLE_IDS = (2, 3, 4, 5, 6, 7, 8, 9, 10)

# M8：角色表（镜像 CharacterDef）。乘区在 Player 初始化时一次性应用
CHARACTERS = {
    'ranger': dict(name='星尘游侠', start=1, hp=1.0, speed=1.0, dmg=1.0,
                   cd=1.0, area=1.0, pickup=1.0, xp=1.15),
    'guardian': dict(name='重装守卫', start=4, hp=1.35, speed=0.90, dmg=1.10,
                     cd=0.88, area=1.20, pickup=1.0, xp=1.0),   # M8 调参：cd 0.92→0.88（G2）
}

# 武器各等级的成长系数（与 C# WeaponLevelBonus / SkillDef.Effective* 对应）
def weapon_stats(wid, level):
    """返回该武器在指定等级下的有效数值。"""
    w = dict(WEAPONS[wid])
    lv = max(1, level)
    t = w['type']
    if t == 'projectile':                      # 星能弹：伤害 +25%/级，Lv3/4 各 +1 弹体
        w['dmg'] *= 1.25 ** (lv - 1)
        if lv >= 3:
            w['count'] += 1
        if lv >= 4:
            w['count'] += 1
            w['pierce'] += 1
    elif t == 'scatter':                       # 散射枪：弹体 +1/级，伤害 +10%/级
        w['dmg'] *= 1.10 ** (lv - 1)
        w['count'] += (lv - 1)
    elif t == 'nova':                          # 新星类：范围 +15%/级，伤害 +20%/级
        w['dmg'] *= 1.20 ** (lv - 1)
        w['radius'] *= 1.15 ** (lv - 1)
    elif t == 'orbit':                         # 雷环刃：刃 +1/级，伤害 +10%/级
        w['dmg'] *= 1.10 ** (lv - 1)
        w['count'] = lv
    elif t == 'chain':                         # 雷霆之链：跳电目标 +1/级，伤害 +18%/级
        w['dmg'] *= 1.18 ** (lv - 1)
        w['jumps'] += (lv - 1)
    elif t == 'boomerang':                     # 月刃：伤害 +22%/级，Lv3/Lv4 各 +1 弹体
        w['dmg'] *= 1.22 ** (lv - 1)
        w['count'] += (1 if lv >= 3 else 0) + (1 if lv >= 4 else 0)
    elif t == 'homing':                        # 蜂群导弹：弹体 +1/级，伤害 +20%/级
        w['dmg'] *= 1.20 ** (lv - 1)
        w['count'] += (lv - 1)
    elif t == 'aura':                          # 圣光领域：范围 +12%/级，伤害 +15%/级
        w['dmg'] *= 1.15 ** (lv - 1)
        w['radius'] *= 1.12 ** (lv - 1)
    elif t == 'mine':                           # 脉冲地雷：伤害 +20%/级，范围 +12%/级，Lv3/Lv4 各 +1 枚
        w['dmg'] *= 1.20 ** (lv - 1)
        w['radius'] *= 1.12 ** (lv - 1)
        w['count'] = w.get('count', 1) + (1 if lv >= 3 else 0) + (1 if lv >= 4 else 0)
    return w


STAT_UPGRADES = [
    ('max_hp',     '最大生命 +20'),
    ('speed',      '移动速度 +8%'),
    ('damage',     '伤害 +15%'),
    ('cooldown',   '冷却 -12%'),
    ('projectile', '弹体 +1'),
    ('pickup',     '拾取范围 +30%'),
    ('area',       '范围 +15%'),
]


# ---------------- 实体 ----------------
_eid_seq = 1   # 敌人实例 id（回旋镖单趟命中去重用）

class Enemy:
    __slots__ = ('kind', 'hp', 'max_hp', 'speed', 'contact', 'score', 'x', 'y',
                 'contact_timer', 'elite', 'slow_t', 'slow_mult', 'alive',
                 'burst_t', 'summon_t', 'eid')

    def __init__(self, kind, hp, speed, x, y, elite=False):
        global _eid_seq
        k = KINDS[kind]
        self.eid = _eid_seq; _eid_seq += 1
        self.kind = kind
        self.max_hp = hp
        self.hp = hp
        self.speed = speed
        self.contact = k['contact'] * (1.6 if elite else 1.0)
        self.score = k['score'] * (CFG['elite_score_mult'] if elite else 1)
        self.x, self.y = x, y
        self.contact_timer = 0.0
        self.elite = elite
        self.slow_t = 0.0
        self.slow_mult = 1.0
        self.alive = True
        self.burst_t = 2.5
        self.summon_t = 6.0

    def take_damage(self, amount):
        self.hp -= amount
        if self.hp <= 0:
            self.alive = False


class Projectile:
    """弹道实体。M8：behavior 0=普通直射/散射 1=回旋镖（飞出返回） 2=追踪导弹。"""
    __slots__ = ('x', 'y', 'dx', 'dy', 'speed', 'dmg', 'pierce', 'life',
                 'behavior', 'origin_x', 'origin_y', 'max_range', 'traveled',
                 'returning', 'hit_set', 'retarget_t', 'turn')

    def __init__(self, x, y, dx, dy, speed, dmg, pierce,
                 behavior=0, max_range=0.0, turn=8.0):
        self.x, self.y = x, y
        self.dx, self.dy = dx, dy
        self.speed = speed
        self.dmg = dmg
        self.pierce = pierce
        self.life = 2.0
        self.behavior = behavior
        self.origin_x, self.origin_y = x, y
        self.max_range = max_range
        self.traveled = 0.0
        self.returning = False
        self.hit_set = set()
        self.retarget_t = 0.0
        self.turn = turn
        if behavior != 0:
            self.life = 4.0 if behavior == 1 else 3.0


# ---------------- 玩家 ----------------
class Player:
    def __init__(self, character='ranger'):
        c = CHARACTERS[character]
        self.character = character
        self.x = self.y = 0.0
        self.hp = CFG['player_hp'] * c['hp']
        self.max_hp = self.hp
        self.speed = CFG['player_speed'] * c['speed']
        self.dmg_mult = c['dmg']
        self.cd_mult = c['cd']
        self.area_mult = c['area']
        self.pickup_radius = CFG['pickup_radius'] * c['pickup']
        self.xp_mult = c['xp']
        self.weapons = {c['start']: 1}    # id -> 等级（角色起始武器）
        self.proj_bonus = 0
        self.level = 1
        self.xp = 0.0
        self.xp_to_next = CFG['xp_base']
        self.kills = 0
        self.timers = {wid: 0.0 for wid in WEAPONS}
        self.orbit_hit_timer = 0.0
        self.aura_hit_timer = 0.0
        self.alive = True
        self.upgrades_taken = []
        self.healed = 0.0

    def has_tags(self):
        tags = set()
        for wid in self.weapons:
            tags.update(WEAPONS[wid]['tags'])
        return tags

    def combo(self):
        """镜像 ComboManager.Compute：规则按数组顺序依次赋值（后写覆盖，不是连乘）。
        fire+storm=×1.5；ice+earth=减速；lightning+fire=穿透+2；holy+fire=×1.4（若三者
        fire+storm+holy 同存，最终伤害乘区为 1.4——与 C# 规则顺序完全一致）。"""
        tags = self.has_tags()
        dmg_mult, pierce_bonus, slow = 1.0, 0, False
        if 'fire' in tags and 'storm' in tags:
            dmg_mult = 1.5
        if 'ice' in tags and 'earth' in tags:
            slow = True
        if 'lightning' in tags and 'fire' in tags:
            pierce_bonus = 2
        if 'holy' in tags and 'fire' in tags:
            dmg_mult = 1.4
        return dict(dmg_mult=dmg_mult, pierce_bonus=pierce_bonus, slow=slow)

    def add_xp(self, amount):
        """增加经验，返回本次提升的等级数（0 表示未升级）。"""
        self.xp += amount * self.xp_mult
        levels = 0
        while self.xp >= self.xp_to_next:
            self.xp -= self.xp_to_next
            self.level += 1
            self.xp_to_next = round(self.xp_to_next * CFG['xp_growth'] + CFG['xp_add'])
            levels += 1
        return levels

    def heal(self, amount):
        before = self.hp
        self.hp = min(self.max_hp, self.hp + amount)
        self.healed += self.hp - before

    def weapon_dps(self):
        combo = self.combo()
        total = 0.0
        for wid, lv in self.weapons.items():
            w = weapon_stats(wid, lv)
            dmg = w['dmg'] * self.dmg_mult * combo['dmg_mult']
            if w['type'] in ('projectile', 'scatter'):
                n = w['count'] + self.proj_bonus
                total += dmg * n / max(0.05, w['cd'] * self.cd_mult)
            elif w['type'] == 'nova':
                total += dmg / max(0.05, w['cd'] * self.cd_mult)
            elif w['type'] == 'orbit':
                total += dmg / w['hit_cd']
            elif w['type'] == 'chain':
                # 跳电衰减求和：dmg * (1 + f + f² + ... )，跳数= EffectiveCount
                jumps = max(1, w['jumps'])
                f = max(0.1, w['falloff'])
                total += dmg * sum(f ** i for i in range(jumps)) / max(0.05, w['cd'] * self.cd_mult)
            elif w['type'] == 'boomerang':
                # 来回各伤一次（对单敌理论上 2 次，群伤折扣后约 1.8）
                total += dmg * 1.8 * w['count'] / max(0.05, w['cd'] * self.cd_mult)
            elif w['type'] == 'homing':
                total += dmg * w['count'] / max(0.05, w['cd'] * self.cd_mult)
            elif w['type'] == 'aura':
                total += dmg / max(0.1, w['interval'])
            elif w['type'] == 'mine':
                # 区域封锁：每次布雷 drop count 枚，各炸一次（模拟近似为玩家处周期 AoE）
                total += dmg * w['count'] / max(0.05, w['cd'] * self.cd_mult)
        return total


def build_upgrade_pool(p):
    """镜像 UpgradeCatalog.BuildOffers：武器解锁 + 武器升级(1..4级) + 属性强化。"""
    pool = []
    for wid in UNLOCKABLE_IDS:
        if wid not in p.weapons:
            pool.append(('unlock', wid))
        elif p.weapons[wid] < CFG['weapon_max_level']:
            pool.append(('weaponlv', wid))
    pool += STAT_UPGRADES
    return pool


def pick_upgrade(p):
    pool = build_upgrade_pool(p)
    random.shuffle(pool)
    choice = random.choice(pool[:3])

    # M8 修复：属性强化也是 tuple（('max_hp', '最大生命 +20')），必须按首元素区分，
    # 否则会被误判成武器强化（KeyError）——该路径自 M3.6 建立起从未被执行，bug 潜伏至今。
    if isinstance(choice, tuple) and choice[0] in ('unlock', 'weaponlv'):
        kind, wid = choice
        if kind == 'unlock':
            p.weapons[wid] = 1
            p.timers.setdefault(wid, 0.0)
        else:
            p.weapons[wid] += 1
        p.upgrades_taken.append('%s:%d' % (kind, wid))
    else:
        key = choice[0]
        if key == 'max_hp':
            p.max_hp += 20
            p.hp = p.max_hp
        elif key == 'speed':
            p.speed *= 1.08
        elif key == 'damage':
            p.dmg_mult *= 1.15
        elif key == 'cooldown':
            p.cd_mult *= 0.88
        elif key == 'projectile':
            p.proj_bonus += 1
        elif key == 'pickup':
            p.pickup_radius *= 1.3
        elif key == 'area':
            p.area_mult *= 1.15          # M8 修复：该分支曾缺失，范围强化是空转的
        # area 影响 AoE 半径，在 weapon_stats 之后乘算
        p.upgrades_taken.append(key)


# ---------------- 波次 ----------------
def pick_kind(wave):
    if wave < 2:
        return 'chaser'
    r = random.random()
    if wave < 4:
        return 'chaser' if r < 0.7 else 'runner'
    if r < 0.5:
        return 'chaser'
    return 'runner' if r < 0.85 else 'tank'


def spawn_enemy(wave, px, py):
    diff = wave_diff(wave)
    kind = pick_kind(wave)
    k = KINDS[kind]
    hp = k['hp'] * diff
    speed = k['speed'] + wave * k['speed_per_wave']
    cap = k.get('speed_cap')
    if cap:
        speed = min(speed, cap)
    ang = random.uniform(0, math.pi * 2)
    x = px + math.cos(ang) * CFG['spawn_radius']
    y = py + math.sin(ang) * CFG['spawn_radius']

    elite = wave >= CFG['elite_from_wave'] and random.random() < min(
        CFG['elite_chance_base'] + wave * CFG['elite_chance_per_wave'], CFG['elite_chance_max'])
    if elite:
        hp *= CFG['elite_hp_mult']
        speed *= 0.85
    return Enemy(kind, hp, speed, x, y, elite)


# ---------------- 单局模拟 ----------------
def simulate(seed, character='ranger'):
    random.seed(seed)

    p = Player(character)
    enemies = []
    projectiles = []
    health_drops = []   # (x, y, amount)

    wave = 0
    to_spawn = 0
    spawn_timer = 0.0
    wave_cooldown = 3.0
    boss_active = False
    boss = None

    t = 0.0
    dt = CFG['dt']
    stats = dict(wave=0, time=0.0, kills=0, level=1, boss_kills=0,
                 peak_alive=0, healed=0.0, upgrades=[], dps=0.0)

    while t < CFG['max_time'] and p.alive:
        steer(p, enemies, boss, dt)

        # ---------- 刷怪 ----------
        if boss_active:
            pass
        elif to_spawn > 0:
            spawn_timer -= dt
            if spawn_timer <= 0:
                enemies.append(spawn_enemy(wave, p.x, p.y))
                to_spawn -= 1
                spawn_timer = max(CFG['spawn_interval_min'],
                                  CFG['spawn_interval'] - wave * CFG['spawn_interval_dec'])
        elif not enemies:
            wave_cooldown -= dt
            if wave_cooldown <= 0:
                wave += 1
                if wave % CFG['boss_every'] == 0:
                    boss_active = True
                    boss_hp_max = CFG['boss_hp_base'] + CFG['boss_hp_per'] * wave
                    boss = Enemy('tank', boss_hp_max, 1.1, p.x, p.y + 6.0, False)
                    boss.contact = 12.0
                else:
                    to_spawn = CFG['wave_count_base'] + wave * CFG['wave_count_per']

        # ---------- 玩家开火 ----------
        combo = p.combo()
        for wid, lv in list(p.weapons.items()):
            w = weapon_stats(wid, lv)
            if w['type'] in ('projectile', 'scatter'):
                p.timers[wid] -= dt
                if p.timers[wid] <= 0:
                    p.timers[wid] = max(0.05, w['cd'] * p.cd_mult)
                    target = find_target(p, enemies, boss, w['rng'])
                    if target is not None:
                        tx, ty = target
                        dx, dy = norm(tx - p.x, ty - p.y)
                        n = w['count'] + p.proj_bonus
                        dmg = w['dmg'] * p.dmg_mult * combo['dmg_mult']
                        pierce = w['pierce'] + combo['pierce_bonus']
                        for i in range(n):
                            spread = (i - (n - 1) / 2.0) * 0.18 if n > 1 else 0.0
                            c, s = math.cos(spread), math.sin(spread)
                            rdx, rdy = dx * c - dy * s, dx * s + dy * c
                            projectiles.append(Projectile(p.x, p.y, rdx, rdy, w['speed'], dmg, pierce))
            elif w['type'] == 'nova':
                p.timers[wid] -= dt
                if p.timers[wid] <= 0:
                    p.timers[wid] = max(0.05, w['cd'] * p.cd_mult)
                    r = w['radius'] * p.area_mult
                    dmg = w['dmg'] * p.dmg_mult * combo['dmg_mult']
                    for e in enemies:
                        if e.alive and dist2(e.x, e.y, p.x, p.y) <= r * r:
                            e.take_damage(dmg)
                            if combo['slow']:
                                e.slow_mult = min(e.slow_mult, 0.5)
                                e.slow_t = max(e.slow_t, 1.5)
                    if boss is not None and boss.alive and dist2(boss.x, boss.y, p.x, p.y) <= r * r:
                        boss.take_damage(dmg)
            elif w['type'] == 'orbit':
                p.orbit_hit_timer -= dt
                if p.orbit_hit_timer <= 0:
                    p.orbit_hit_timer = w['hit_cd']
                    r = w['radius']
                    dmg = w['dmg'] * p.dmg_mult * combo['dmg_mult']
                    for e in enemies:
                        if e.alive and abs(math.sqrt(dist2(e.x, e.y, p.x, p.y)) - r) < 0.6:
                            e.take_damage(dmg)
                    if boss is not None and boss.alive and abs(math.sqrt(dist2(boss.x, boss.y, p.x, p.y)) - r) < 0.6:
                        boss.take_damage(dmg)
            elif w['type'] == 'chain':
                # 雷霆之链：瞬时跳电（镜像 FireChain——首跳索敌范围，之后 chain_range，逐跳 ×falloff）
                p.timers[wid] -= dt
                if p.timers[wid] <= 0:
                    p.timers[wid] = max(0.05, w['cd'] * p.cd_mult)
                    dmg_base = w['dmg'] * p.dmg_mult * combo['dmg_mult']
                    jumps = max(1, w['jumps'])
                    fx, fy = p.x, p.y
                    search = w['rng']
                    hit_ids = set()
                    for i in range(jumps):
                        tgt = None; best_d = search * search
                        for e in enemies:
                            if not e.alive or e.eid in hit_ids:
                                continue
                            d2 = dist2(e.x, e.y, fx, fy)
                            if d2 < best_d:
                                best_d = d2; tgt = e
                        if boss is not None and boss.alive and boss.eid not in hit_ids:
                            d2 = dist2(boss.x, boss.y, fx, fy)
                            if d2 < best_d:
                                best_d = d2; tgt = boss
                        if tgt is None:
                            break
                        hit_ids.add(tgt.eid)
                        tgt.take_damage(dmg_base * (max(0.1, w['falloff']) ** i))
                        fx, fy = tgt.x, tgt.y
                        search = w['chain_range']
            elif w['type'] == 'boomerang':
                # 月刃：朝最近敌飞出，到达最远距离后原路返回（命中去重见弹道推进）
                p.timers[wid] -= dt
                if p.timers[wid] <= 0:
                    p.timers[wid] = max(0.05, w['cd'] * p.cd_mult)
                    target = find_target(p, enemies, boss, w['rng'])
                    if target is not None:
                        tx, ty = target
                        dx, dy = norm(tx - p.x, ty - p.y)
                        dmg = w['dmg'] * p.dmg_mult * combo['dmg_mult']
                        for i in range(w['count']):
                            spread = (i - (w['count'] - 1) / 2.0) * 0.25 if w['count'] > 1 else 0.0
                            c, s = math.cos(spread), math.sin(spread)
                            rdx, rdy = dx * c - dy * s, dx * s + dy * c
                            projectiles.append(Projectile(p.x, p.y, rdx, rdy, w['speed'],
                                                          dmg, 0, behavior=1, max_range=w['max_range']))
            elif w['type'] == 'homing':
                # 蜂群导弹：自动追踪（0.15s 重锁 + 限速转向，镜像 HomingMissile）
                p.timers[wid] -= dt
                if p.timers[wid] <= 0:
                    p.timers[wid] = max(0.05, w['cd'] * p.cd_mult)
                    target = find_target(p, enemies, boss, w['rng'])
                    dx, dy = (norm(target[0] - p.x, target[1] - p.y) if target
                              else norm(random.uniform(-1, 1), random.uniform(-1, 1)))
                    dmg = w['dmg'] * p.dmg_mult * combo['dmg_mult']
                    for i in range(w['count']):
                        spread = (i - (w['count'] - 1) / 2.0) * 0.3 if w['count'] > 1 else 0.0
                        c, s = math.cos(spread), math.sin(spread)
                        rdx, rdy = dx * c - dy * s, dx * s + dy * c
                        projectiles.append(Projectile(p.x, p.y, rdx, rdy, w['speed'],
                                                      dmg, 0, behavior=2, turn=w['turn']))
            elif w['type'] == 'aura':
                # 圣光领域：持续伤害（整圆，无距离环判定），与 C# UpdateAura 一致
                p.aura_hit_timer -= dt
                if p.aura_hit_timer <= 0:
                    p.aura_hit_timer = max(0.1, w['interval'])
                    r = w['radius'] * p.area_mult
                    dmg = w['dmg'] * p.dmg_mult * combo['dmg_mult']
                    for e in enemies:
                        if e.alive and dist2(e.x, e.y, p.x, p.y) <= r * r:
                            e.take_damage(dmg)
                            if combo['slow']:
                                e.slow_mult = min(e.slow_mult, 0.5)
                                e.slow_t = max(e.slow_t, 1.5)
                    if boss is not None and boss.alive and dist2(boss.x, boss.y, p.x, p.y) <= r * r:
                        boss.take_damage(dmg)
            elif w['type'] == 'mine':
                # 脉冲地雷（模拟近似：玩家处周期 AoE 爆发，覆盖 drop 的 count 枚总伤）
                p.timers[wid] -= dt
                if p.timers[wid] <= 0:
                    p.timers[wid] = max(0.05, w['cd'] * p.cd_mult)
                    r = w['radius'] * p.area_mult
                    dmg = w['dmg'] * p.dmg_mult * combo['dmg_mult'] * w['count']
                    for e in enemies:
                        if e.alive and dist2(e.x, e.y, p.x, p.y) <= r * r:
                            e.take_damage(dmg)
                            if combo['slow']:
                                e.slow_mult = min(e.slow_mult, 0.5)
                                e.slow_t = max(e.slow_t, 1.5)
                    if boss is not None and boss.alive and dist2(boss.x, boss.y, p.x, p.y) <= r * r:
                        boss.take_damage(dmg)

        # ---------- 弹道推进 ----------
        for pr in projectiles:
            if pr.behavior == 1:
                # 回旋镖：飞出→折返→回到起点消失；每趟（去/回）对同一敌人只伤一次
                step = pr.speed * dt
                if not pr.returning:
                    pr.x += pr.dx * step
                    pr.y += pr.dy * step
                    pr.traveled += step
                    if pr.traveled >= pr.max_range:
                        pr.returning = True
                        pr.hit_set.clear()
                else:
                    tx, ty = pr.origin_x - pr.x, pr.origin_y - pr.y
                    remain = math.hypot(tx, ty)
                    if remain <= step:
                        pr.life = 0   # 回起点即消失
                        continue
                    pr.x += tx / remain * step
                    pr.y += ty / remain * step
                pr.life -= dt
                if pr.life <= 0:
                    continue
                for e in enemies:
                    if not e.alive or e.eid in pr.hit_set:
                        continue
                    if dist2(pr.x, pr.y, e.x, e.y) <= (0.4 + 0.4) ** 2:
                        pr.hit_set.add(e.eid)
                        e.take_damage(pr.dmg)
                        if combo['slow']:
                            e.slow_mult = min(e.slow_mult, 0.5)
                            e.slow_t = max(e.slow_t, 1.5)
                if boss is not None and boss.alive and boss.eid not in pr.hit_set \
                        and dist2(pr.x, pr.y, boss.x, boss.y) <= (0.4 + 1.0) ** 2:
                    pr.hit_set.add(boss.eid)
                    boss.take_damage(pr.dmg)
                continue
            if pr.behavior == 2:
                # 追踪导弹：0.15s 重锁最近敌，限速转向（镜像 HomingMissile）
                pr.retarget_t -= dt
                if pr.retarget_t <= 0:
                    pr.retarget_t = 0.15
                    target = find_target(p, enemies, boss, 12.0)
                    if target is not None:
                        tdx, tdy = norm(target[0] - pr.x, target[1] - pr.y)
                        cdx, cdy = pr.dx, pr.dy
                        dot = max(-1.0, min(1.0, cdx * tdx + cdy * tdy))
                        ang = math.acos(dot)
                        max_ang = pr.turn * dt
                        if ang > max_ang:
                            k = max_ang / ang
                            tdx, tdy = cdx + (tdx - cdx) * k, cdy + (tdy - cdy) * k
                        n = math.hypot(tdx, tdy)
                        if n > 1e-9:
                            pr.dx, pr.dy = tdx / n, tdy / n
                pr.x += pr.dx * pr.speed * dt
                pr.y += pr.dy * pr.speed * dt
                pr.life -= dt
                if pr.life <= 0:
                    continue
                for e in enemies:
                    if not e.alive:
                        continue
                    if dist2(pr.x, pr.y, e.x, e.y) <= (0.35 + 0.4) ** 2:
                        e.take_damage(pr.dmg)
                        if combo['slow']:
                            e.slow_mult = min(e.slow_mult, 0.5)
                            e.slow_t = max(e.slow_t, 1.5)
                        pr.life = 0
                        break
                if pr.life > 0 and boss is not None and boss.alive \
                        and dist2(pr.x, pr.y, boss.x, boss.y) <= (0.35 + 1.0) ** 2:
                    boss.take_damage(pr.dmg)
                    pr.life = 0
                continue

            pr.x += pr.dx * pr.speed * dt
            pr.y += pr.dy * pr.speed * dt
            pr.life -= dt
            if pr.life <= 0:
                continue
            for e in enemies:
                if not e.alive:
                    continue
                if abs(pr.x - e.x) > 1.5 or abs(pr.y - e.y) > 1.5:
                    continue
                rr = CFG['proj_hit_radius'] + (0.45 if e.elite else 0.0)
                if dist2(pr.x, pr.y, e.x, e.y) <= rr * rr:
                    e.take_damage(pr.dmg)
                    if combo['slow']:
                        e.slow_mult = min(e.slow_mult, 0.5)
                        e.slow_t = max(e.slow_t, 1.5)
                    if pr.pierce > 0:
                        pr.pierce -= 1
                    else:
                        pr.life = 0
                        break
            if pr.life > 0 and boss is not None and boss.alive:
                if dist2(pr.x, pr.y, boss.x, boss.y) <= CFG['boss_hit_radius'] ** 2:
                    boss.take_damage(pr.dmg)
                    pr.life = 0

        # ---------- 敌人 AI ----------
        for e in enemies:
            if not e.alive:
                continue
            if e.slow_t > 0:
                e.slow_t -= dt
                if e.slow_t <= 0:
                    e.slow_mult = 1.0
            dx, dy = norm(p.x - e.x, p.y - e.y)
            e.x += dx * e.speed * e.slow_mult * dt
            e.y += dy * e.speed * e.slow_mult * dt

            e.contact_timer -= dt
            if e.contact_timer <= 0 and dist2(e.x, e.y, p.x, p.y) <= CFG['contact_dist'] ** 2:
                p.hp -= e.contact
                e.contact_timer = CFG['contact_cd']

        # ---------- Boss 行为 ----------
        if boss is not None and boss.alive:
            dx, dy = norm(p.x - boss.x, p.y - boss.y)
            boss.x += dx * 1.1 * dt
            boss.y += dy * 1.1 * dt
            boss.contact_timer -= dt
            if boss.contact_timer <= 0 and dist2(boss.x, boss.y, p.x, p.y) <= 1.3 ** 2:
                p.hp -= 12.0
                boss.contact_timer = 0.6
            boss.burst_t -= dt
            if boss.burst_t <= 0:
                boss.burst_t = 2.3
                for i in range(10):
                    a = i * math.pi * 2 / 10
                    projectiles.append(Projectile(boss.x, boss.y, math.cos(a), math.sin(a), 6.5, 9.0, -1))
            boss.summon_t -= dt
            if boss.summon_t <= 0:
                boss.summon_t = 9.0
                for i in range(2):
                    ang = random.uniform(0, math.pi * 2)
                    rr = random.uniform(0, 2.5)
                    enemies.append(Enemy('runner', 16.0, 3.6,
                                         boss.x + math.cos(ang) * rr, boss.y + math.sin(ang) * rr))

        # ---------- 敌方弹幕命中玩家 ----------
        for pr in projectiles:
            if pr.pierce == -1 and pr.life > 0:
                if dist2(pr.x, pr.y, p.x, p.y) <= 0.5 ** 2:
                    p.hp -= pr.dmg
                    pr.life = 0

        # ---------- 回血球：磁吸与拾取 ----------
        for i in range(len(health_drops) - 1, -1, -1):
            hx, hy, amt = health_drops[i]
            d = math.hypot(hx - p.x, hy - p.y)
            if d <= 0.4:
                p.heal(amt)
                health_drops.pop(i)
            elif d <= p.pickup_radius:
                step = 12.0 * dt
                nx = hx + (p.x - hx) / d * step
                ny = hy + (p.y - hy) / d * step
                health_drops[i] = (nx, ny, amt)

        # ---------- 清理与统计 ----------
        dead = [e for e in enemies if not e.alive]
        for e in dead:
            p.kills += 1
            # M8 修复：升级选卡此前从未被调用（M3.6 起潜伏 bug）——每次升级抽一张卡
            for _ in range(p.add_xp(e.score)):
                pick_upgrade(p)
            if e.elite:
                health_drops.append((e.x, e.y, CFG['elite_health_amount']))
            elif random.random() < CFG['health_drop_chance']:
                health_drops.append((e.x, e.y, CFG['health_drop_amount']))
        enemies = [e for e in enemies if e.alive]
        projectiles = [pr for pr in projectiles if pr.life > 0]

        if boss is not None and not boss.alive:
            stats['boss_kills'] += 1
            for i in range(14):
                for _ in range(p.add_xp(4.0)):
                    pick_upgrade(p)
            health_drops.append((boss.x, boss.y, CFG['boss_health_amount']))
            boss = None
            boss_active = False
            wave_cooldown = 3.0

        stats['peak_alive'] = max(stats['peak_alive'], len(enemies))

        if p.hp <= 0:
            p.alive = False
            break

        t += dt

    stats['wave'] = wave
    stats['time'] = t
    stats['level'] = p.level
    stats['kills'] = p.kills
    stats['hp_ratio_left'] = max(0.0, p.hp) / p.max_hp
    stats['dps'] = p.weapon_dps()
    stats['healed'] = p.healed
    stats['upgrades'] = p.upgrades_taken
    return stats


def norm(x, y):
    n = math.hypot(x, y)
    return (x / n, y / n) if n > 1e-9 else (0.0, 0.0)


def dist2(x1, y1, x2, y2):
    return (x1 - x2) ** 2 + (y1 - y2) ** 2


def steer(p, enemies, boss, dt):
    """16 方向势场走位：强斥力避免贴身 + 偏好约 5 格站位 + 保持敌人在射程内。"""
    best_dir = (0.0, 0.0)
    best_score = -1e9
    step = p.speed * dt
    for i in range(16):
        a = i * math.pi / 8.0
        dx, dy = math.cos(a), math.sin(a)
        nx, ny = p.x + dx * step, p.y + dy * step

        dmin = 60.0
        in_range = 0
        repulsion = 0.0
        for e in enemies:
            if not e.alive:
                continue
            ex = e.x - nx
            if ex > 12.0 or ex < -12.0:
                continue
            ey = e.y - ny
            if ey > 12.0 or ey < -12.0:
                continue
            d = math.hypot(ex, ey)
            if d < dmin:
                dmin = d
            if d <= 7.0:
                in_range += 1
            if d < 6.0:
                repulsion += 1.0 / (d * d + 0.3)
        if boss is not None and boss.alive:
            d = math.hypot(boss.x - nx, boss.y - ny)
            if d < dmin:
                dmin = d
            if d <= 7.0:
                in_range += 3

        score = (in_range * 0.5
                 - repulsion * 4.0
                 - abs(dmin - 5.0) * 1.2
                 - max(0.0, 1.2 - dmin) * 30.0)
        if score > best_score:
            best_score = score
            best_dir = (dx, dy)
    p.x += best_dir[0] * step
    p.y += best_dir[1] * step


def find_target(p, enemies, boss, rng):
    best = None
    best_d = rng * rng
    if boss is not None and boss.alive:
        bd = dist2(p.x, p.y, boss.x, boss.y)
        if bd < best_d:
            best_d = bd
            best = (boss.x, boss.y)
    for e in enemies:
        if not e.alive:
            continue
        d = dist2(p.x, p.y, e.x, e.y)
        if d < best_d:
            best_d = d
            best = (e.x, e.y)
    return best


# ---------------- 批量运行与报告 ----------------
def _report_block(title, results):
    waves = sorted(r['wave'] for r in results)
    times = sorted(r['time'] for r in results)
    levels = sorted(r['level'] for r in results)
    kills = sorted(r['kills'] for r in results)

    def pct(arr, q):
        if not arr:
            return 0
        return arr[min(len(arr) - 1, int(q * len(arr)))]

    print('【%s】 trials=%d' % (title, len(results)))
    print('存活波次   : 中位 %.0f | P10 %.0f | P90 %.0f | 最高 %d'
          % (pct(waves, 0.5), pct(waves, 0.1), pct(waves, 0.9), waves[-1]))
    print('存活时间(s): 中位 %.0f (%.1f min) | P10 %.0f | P90 %.0f | 最长 %.0f'
          % (pct(times, 0.5), pct(times, 0.5) / 60.0, pct(times, 0.1), pct(times, 0.9), times[-1]))
    print('达成等级   : 中位 %.0f | P10 %.0f | P90 %.0f' % (pct(levels, 0.5), pct(levels, 0.1), pct(levels, 0.9)))
    print('累计击杀   : 中位 %.0f | P90 %.0f' % (pct(kills, 0.5), pct(kills, 0.9)))
    print('累计回血   : 中位 %.0f HP' % statistics.median([r['healed'] for r in results]))
    print('Boss 击杀  : 平均 %.2f 个' % statistics.mean([r['boss_kills'] for r in results]))
    print('同屏敌人峰值(P90): %.0f' % pct(sorted(r['peak_alive'] for r in results), 0.9))
    print('死亡波次分布: ' + ' '.join('%d:%d' % (w, waves.count(w)) for w in sorted(set(waves))))
    # M8：武器强化采用率（解锁+升级计次 / trials，>1 表示单局多次强化同一把）
    adopt = {}
    for r in results:
        for u in r['upgrades']:
            k, _, wid = u.partition(':')
            if wid:
                adopt[int(wid)] = adopt.get(int(wid), 0) + 1
    if adopt:
        line = '  '.join('%s %.2f' % (WEAPONS[wid]['name'], adopt[wid] / len(results))
                         for wid in sorted(adopt))
        print('武器强化采用率(次/局): ' + line)


def run(n_trials=60, seed0=1234):
    print('=' * 70)
    print('《星界像素狂潮》平衡模拟报告（M9：10 武器 + 双角色）   trials/角色=%d' % n_trials)
    print('=' * 70)
    by_char = {}
    for ch in ('ranger', 'guardian'):
        results = [simulate(seed0 + i, ch) for i in range(n_trials)]
        by_char[ch] = results
        _report_block(CHARACTERS[ch]['name'], results)
        print('-' * 70)

    # 角色对比（同种子对局，活得更深的一方胜）
    r_waves = [r['wave'] for r in by_char['ranger']]
    g_waves = [r['wave'] for r in by_char['guardian']]
    deeper_r = sum(1 for a, b in zip(r_waves, g_waves) if a > b)
    deeper_g = sum(1 for a, b in zip(r_waves, g_waves) if b > a)
    print('角色对比（同种子对局）: 游侠更深 %d / 守卫更深 %d / 平 %d'
          % (deeper_r, deeper_g, n_trials - deeper_r - deeper_g))
    print('-' * 70)

    print('曲线诊断：')
    for w in (1, 3, 5, 10, 15, 20, 25, 30, 40, 50, 60):
        diff = wave_diff(w)
        cnt = CFG['wave_count_base'] + w * CFG['wave_count_per']
        runner_sp = KINDS['runner']['speed'] + w * KINDS['runner']['speed_per_wave']
        cap = KINDS['runner'].get('speed_cap')
        if cap:
            runner_sp = min(runner_sp, cap)
        print('  Wave %2d: 敌数 %3d | 追击HP %5.1f | 重甲HP %6.1f | 追击速度 %.2f | 疾行速度 %.2f | 刷怪间隔 %.3fs'
              % (w, cnt, KINDS['chaser']['hp'] * diff, KINDS['tank']['hp'] * diff,
                 KINDS['chaser']['speed'] + w * KINDS['chaser']['speed_per_wave'],
                 runner_sp,
                 max(CFG['spawn_interval_min'], CFG['spawn_interval'] - w * CFG['spawn_interval_dec'])))
    print('-' * 70)

    print('告警：')
    warn = []
    catch = None
    for w in range(1, 300):
        sp = KINDS['runner']['speed'] + w * KINDS['runner']['speed_per_wave']
        cap = KINDS['runner'].get('speed_cap')
        if cap:
            sp = min(sp, cap)
        if sp >= CFG['player_speed']:
            catch = w
            break
    if catch:
        warn.append('疾行怪速度在 Wave %d 起超过玩家基础移速 %.1f（未计移速强化/减速联组时无法风筝）'
                    % (catch, CFG['player_speed']))
    base_dps = Player('ranger').weapon_dps()
    warn.append('Wave 10 Boss HP = %.0f；以游侠初始 DPS %.0f 计需 %.0f 秒纯输出'
                % (CFG['boss_hp_base'] + CFG['boss_hp_per'] * 10,
                   base_dps, (CFG['boss_hp_base'] + CFG['boss_hp_per'] * 10) / base_dps))
    cum = 0.0
    cum_by_wave = {}
    for w in range(1, 80):
        cum += (CFG['wave_count_base'] + w * CFG['wave_count_per']) * 1.2
        if w % 5 == 0:
            cum += 14 * 4.0
        cum_by_wave[w] = cum
    need = CFG['xp_base']
    acc = 0.0
    lvl_xp = {}
    for lv in range(2, 31):
        acc += need
        lvl_xp[lv] = acc
        need = round(need * CFG['xp_growth'] + CFG['xp_add'])
    for tl in (5, 10, 15, 20, 25):
        wv = next((w for w in range(1, 80) if cum_by_wave[w] >= lvl_xp[tl]), None)
        warn.append('达到 Lv %d 需 %d XP → 约 Wave %s' % (tl, lvl_xp[tl], wv if wv else '>79'))
    for line in warn:
        print('  ⚠ ' + line)
    print('=' * 70)
    return by_char


if __name__ == '__main__':
    run()
