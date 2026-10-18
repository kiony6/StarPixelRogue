#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""M8 平衡调参实验台：在 balance_sim 基础上叠加参数候选，快速对比。

用法：
    python balance_experiments.py            # 跑全部预设候选
    python balance_experiments.py OLD P1     # 只跑指定候选
    python balance_experiments.py --n 60     # 指定每角色 trials 数

指标看两个：中位死亡波次/时间，以及「撞 900s 上限幸存率」——
幸存率 >50% 说明难度仍追不上 build 成长（对割草游戏而言偏简单）。
M8 方案 A+B 验证：OLD=工程现状对照，P1=新基线（分段难度+疾行怪封顶+守卫CD），P2/P3=微调变体。

实现说明：单遍计算（每种子双角色各跑一次，指标+同种子胜率都取自这批结果），
并用多进程并行——每个 worker 进程独立 import balance_sim，在进程内应用候选参数。
"""
import sys
import statistics
from concurrent.futures import ProcessPoolExecutor
import balance_sim as bs

# worker 进程导入时的默认值快照（_run_one 每任务据此还原，避免跨任务残留）
_DEFAULTS = dict(
    cfg=dict(bs.CFG),
    chars={k: dict(v) for k, v in bs.CHARACTERS.items()},
    kinds={k: dict(v) for k, v in bs.KINDS.items()},
)

# 预设候选（相对当前基线的增量）
# 说明：sim 默认值 = 方案 A+B 落地后的新基线（分段难度 knee30/0.18、疾行怪封顶 5.2、守卫 cd0.88）。
# OLD 用于还原「工程现状」做对照；P1/P2/P3 是新基线的三个微调变体。
CANDIDATES = {
    'OLD': dict(cfg={'diff_knee_wave': 0, 'runner_speed_cap': 0},
                chars={'guardian': dict(cd=0.92)},
                kinds={'runner': dict(speed_cap=0)}),          # 工程现状（对照）
    'P1': dict(cfg={}, chars={}, kinds={}),                   # 新基线：knee30 / late0.18 / cap5.2 / cd0.88
    'P2': dict(cfg={'diff_per_wave_late': 0.16}, chars={}, kinds={}),   # 后段更缓
    'P3': dict(cfg={'diff_knee_wave': 35, 'diff_per_wave_late': 0.20}, chars={}, kinds={}),  # 拐点更晚更陡
}


def _run_one(task):
    """worker：独立进程内应用候选参数后跑一局。
    注意：进程内 CFG/CHARACTERS/KINDS 跨任务残留，必须先还原导入时的默认值再套候选，
    否则空配置候选会继承上一个候选改过的键（曾导致 P1/P2 假冒基线数据）。"""
    name, ch, seed, cfg, char_over, kind_over = task
    bs.CFG.clear(); bs.CFG.update(_DEFAULTS['cfg'])
    bs.CFG.update(cfg)
    bs.CHARACTERS.clear()
    bs.CHARACTERS.update({k: dict(v) for k, v in _DEFAULTS['chars'].items()})
    for c, ov in char_over.items():
        bs.CHARACTERS[c].update(ov)
    bs.KINDS.clear()
    bs.KINDS.update({k: dict(v) for k, v in _DEFAULTS['kinds'].items()})
    for kd, ov in kind_over.items():
        bs.KINDS[kd].update(ov)
    r = bs.simulate(seed, ch)
    return name, ch, seed, r['wave'], r['time']


def run_candidates(names, n=40, seed0=1234, workers=8):
    tasks = []
    for name in names:
        spec = CANDIDATES[name]
        for ch in ('ranger', 'guardian'):
            for i in range(n):
                tasks.append((name, ch, seed0 + i, spec['cfg'], spec['chars'],
                              spec.get('kinds', {})))

    results = {}
    with ProcessPoolExecutor(max_workers=workers) as ex:
        for name, ch, seed, wave, t in ex.map(_run_one, tasks, chunksize=2):
            results.setdefault((name, ch), {})[seed] = (wave, t)

    out = {}
    for name in names:
        entry = {}
        r_map = results[(name, 'ranger')]
        g_map = results[(name, 'guardian')]
        for ch, runs_map in (('ranger', r_map), ('guardian', g_map)):
            waves = sorted(w for w, _ in runs_map.values())
            times = [t for _, t in runs_map.values()]
            capped = sum(1 for t in times if t >= bs.CFG['max_time'] - 1)
            entry[ch] = dict(
                med_wave=statistics.median(waves),
                med_time=statistics.median(times),
                p10_wave=waves[n // 10],
                p90_wave=waves[(n * 9) // 10],
                capped_pct=100.0 * capped / n,
            )
        # 同种子胜率
        entry['ranger_win'] = 100.0 * sum(
            1 for s in r_map if r_map[s][0] > g_map[s][0]) / n
        out[name] = entry
    return out


def main():
    argv = sys.argv[1:]
    n, names = 40, []
    i = 0
    while i < len(argv):
        if argv[i] == '--n':
            n = int(argv[i + 1]); i += 2
        else:
            names.append(argv[i]); i += 1
    names = names or list(CANDIDATES)

    print('%-4s %-8s %8s %8s %8s %8s %9s' %
          ('候选', '角色', '中位波', '中位时', 'P10波', 'P90波', '撞限率'), flush=True)
    out = run_candidates(names, n=n)
    for name in names:
        entry = out[name]
        for ch in ('ranger', 'guardian'):
            d = entry[ch]
            print('%-4s %-8s %8.0f %8.0f %8.0f %8.0f %8.0f%%' %
                  (name, ch, d['med_wave'], d['med_time'], d['p10_wave'],
                   d['p90_wave'], d['capped_pct']), flush=True)
        print('     游侠同种子胜率: %.0f%%' % entry['ranger_win'], flush=True)
        print('-' * 64, flush=True)


if __name__ == '__main__':
    main()
