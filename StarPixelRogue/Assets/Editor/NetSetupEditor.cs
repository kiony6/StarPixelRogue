#if FISHNET_ENABLED
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace StarPixel.EditorTools
{
    /// <summary>
    /// Fish-Net 一键装配（菜单：StarPixel / Setup FishNet 联机）。
    /// 生成内容：
    /// - Assets/Net/Prefabs/ 下 7 个网络预制体（玩家/敌人/宝石/回血/玩家弹/敌方弹/世界状态）；
    /// - Assets/Scenes/NetBootstrap.unity：MainCamera + NetworkManager(Tugboat + PlayerSpawner) + NetConfig + EventSystem；
    /// - 自动注册可生成预制体、设置 PlayerSpawner 的玩家预制体、配置各预制体 NetworkTransform 权威模式。
    ///
    /// 说明：Fish-Net 各版本成员名/签名可能有差异，本脚本对不确定的 API 一律用反射 + 多候选名，
    /// 无法自动完成时会在 Console 输出明确警告，此时按 README「联机手动装配」步骤在检视面板补配置即可。
    /// 目标版本：Fish-Net V4（com.firstgeargames.fishnet，UPM git: ?path=Assets/FishNet）。
    /// </summary>
    public static class NetSetupEditor
    {
        private const string PrefabDir = "Assets/Net/Prefabs";
        private const string ScenePath = "Assets/Scenes/NetBootstrap.unity";

        [MenuItem("StarPixel/Setup FishNet 联机装配")]
        public static void Setup()
        {
            if (!Directory.Exists(PrefabDir)) Directory.CreateDirectory(PrefabDir);

            var player = BuildPlayerPrefab();
            var enemy = BuildEnemyPrefab();
            var gem = BuildPickupPrefab("NetGem", "gem");
            var health = BuildPickupPrefab("NetHealth", "health");
            var playerProj = BuildProjectilePrefab("NetPlayerProjectile", new Color(1f, 0.9f, 0.3f));
            var enemyProj = BuildProjectilePrefab("NetEnemyProjectile", new Color(1f, 0.35f, 0.35f));
            var worldState = BuildWorldStatePrefab();

            BuildScene(player, enemy, gem, health, playerProj, enemyProj, worldState);

            Debug.Log("[StarPixel] Fish-Net 装配完成：\n" +
                      "  预制体目录：" + PrefabDir + "\n" +
                      "  场景：" + ScenePath + "\n" +
                      "  下一步：Build Settings 加入 NetBootstrap 场景，运行时先开房主、再加入（见 README M4）。");
        }

        // —— 预制体构建 ——

        private static GameObject BuildPlayerPrefab()
        {
            var go = new GameObject("NetPlayer");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Core.SpriteFactory.GetSprite("player", Color.cyan);
            sr.color = Color.white;
            sr.sortingOrder = 10;
            go.transform.localScale = Vector3.one * 0.5f;

            var dmg = go.AddComponent<Combat.Damageable>();
            dmg.Init(100f);

            go.AddComponent<Player.PlayerController>();
            AddNetComponents(go, "StarPixel.Net.NetPlayer");
            // 玩家：客户端权威移动（V4 NetworkTransform._clientAuthoritative 默认即为 true，显式确保）
            SetClientAuthoritative(go, true);
            return SavePrefab(go, "NetPlayer");
        }

        private static GameObject BuildEnemyPrefab()
        {
            var go = new GameObject("NetEnemy");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Core.SpriteFactory.GetSprite("enemy_chaser", Color.red);
            sr.color = Color.white;
            sr.sortingOrder = 5;
            go.transform.localScale = Vector3.one * 0.45f;

            var dmg = go.AddComponent<Combat.Damageable>();
            dmg.Init(20f);

            AddNetComponents(go, "StarPixel.Net.NetEnemy");
            // 敌人：服务端权威（房主 AI 驱动位置），绝不能客户端权威
            SetClientAuthoritative(go, false);
            return SavePrefab(go, "NetEnemy");
        }

        private static GameObject BuildPickupPrefab(string name, string sprite)
        {
            var go = new GameObject(name);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Core.SpriteFactory.GetSprite(sprite, Color.white);
            sr.color = Color.white;
            sr.sortingOrder = 2;
            go.transform.localScale = Vector3.one * (sprite == "gem" ? 0.2f : 0.22f);
            AddNetComponents(go, "StarPixel.Net.NetPickup");
            SetClientAuthoritative(go, false);   // 掉落物磁吸在房主侧模拟
            return SavePrefab(go, name);
        }

        private static GameObject BuildProjectilePrefab(string name, Color tint)
        {
            var go = new GameObject(name);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Core.SpriteFactory.GetSprite("projectile", Color.yellow);
            sr.color = tint;
            sr.sortingOrder = 8;
            go.transform.localScale = Vector3.one * 0.25f;
            AddNetComponents(go, "StarPixel.Net.NetProjectile");
            SetClientAuthoritative(go, false);   // 弹道飞行与命中判定在房主侧
            return SavePrefab(go, name);
        }

        private static GameObject BuildWorldStatePrefab()
        {
            var go = new GameObject("NetWorldState");
            AddNetComponents(go, "StarPixel.Net.NetWorldState", withTransform: false);
            return SavePrefab(go, "NetWorldState");
        }

        // —— Fish-Net 组件装配（反射 + 多候选类型名，跨版本兼容）——

        private static void AddNetComponents(GameObject go, string scriptTypeName, bool withTransform = true)
        {
            var scriptType = FindType(scriptTypeName);
            if (scriptType == null)
            {
                Debug.LogWarning("[StarPixel] 未找到脚本类型 " + scriptType + "（应位于 Assets/_Project/Scripts/Net/）");
            }
            else
            {
                go.AddComponent(scriptType);
            }

            var noType = FindType("FishNet.Object.NetworkObject") ?? FindType("FishNet.NetworkObject");
            if (noType == null) Debug.LogWarning("[StarPixel] 未找到 FishNet NetworkObject，请确认已安装 Fish-Net 包");
            else go.AddComponent(noType);

            if (withTransform)
            {
                var ntType = FindType("FishNet.Component.Transforming.NetworkTransform")
                             ?? FindType("FishNet.Component.NetworkTransform")
                             ?? FindType("FishNet.Object.NetworkTransform")
                             ?? FindType("FishNet.NetworkTransform");
                if (ntType == null)
                    Debug.LogWarning("[StarPixel] 未找到 NetworkTransform，位置同步需在检视面板手动添加");
                else go.AddComponent(ntType);
            }
        }

        /// <summary>
        /// 设置 NetworkTransform 的客户端权威开关。
        /// V4 起该字段为私有序列化字段 <c>_clientAuthoritative</c>（默认 true），无公共属性，
        /// 因此用反射同时尝试公共属性与私有字段的多种候选名。
        /// </summary>
        private static void SetClientAuthoritative(GameObject go, bool clientAuthoritative)
        {
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                var t = c.GetType();
                if (!t.Name.Contains("NetworkTransform")) continue;

                string[] candidates = { "ClientAuthoritative", "IsClientAuthoritative", "clientAuthoritative", "_clientAuthoritative" };
                foreach (var name in candidates)
                {
                    var prop = t.GetProperty(name);
                    if (prop != null && prop.PropertyType == typeof(bool) && prop.CanWrite)
                    {
                        prop.SetValue(c, clientAuthoritative, null);
                        return;
                    }
                    var field = t.GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (field != null && field.FieldType == typeof(bool))
                    {
                        field.SetValue(c, clientAuthoritative);
                        return;
                    }
                }
                Debug.LogWarning("[StarPixel] 未能自动设置 NetworkTransform 权威模式（" + t.Name +
                                "），请在检视面板确认 Client Authoritative = " + clientAuthoritative);
                return;
            }
        }

        // —— 场景构建 ——

        private static void BuildScene(GameObject player, GameObject enemy, GameObject gem,
            GameObject health, GameObject playerProj, GameObject enemyProj, GameObject worldState)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 相机
            var camGo = new GameObject("MainCamera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 8f;
            cam.backgroundColor = new Color(0.05f, 0.05f, 0.08f);
            camGo.transform.position = new Vector3(0, 0, -10);

            // 事件系统
            var es = new GameObject("EventSystem");
            es.AddComponent<UnityEngine.EventSystems.EventSystem>();
            es.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();

            // NetworkManager（含 Tugboat 传输层 + PlayerSpawner 玩家自动生成）
            var nmGo = new GameObject("NetworkManager");
            var nmType = FindType("FishNet.Managing.NetworkManager") ?? FindType("FishNet.NetworkManager");
            if (nmType == null)
            {
                Debug.LogWarning("[StarPixel] 未找到 NetworkManager 类型，场景中需手动添加 NetworkManager 组件");
            }
            else
            {
                var nm = nmGo.AddComponent(nmType) as Component;
                AddDefaultTransport(nm);
                RegisterSpawnables(nm as object, player, enemy, gem, health, playerProj, enemyProj, worldState);
                AddPlayerSpawner(nmGo, player);
            }

            // 配置物体（运行时 NetConfig.Instance 的来源）
            var cfgGo = new GameObject("NetConfig");
            var cfg = cfgGo.AddComponent<Net.NetConfig>();
            cfg.playerPrefab = player;
            cfg.enemyPrefab = enemy;
            cfg.gemPrefab = gem;
            cfg.healthPrefab = health;
            cfg.playerProjectilePrefab = playerProj;
            cfg.enemyProjectilePrefab = enemyProj;
            cfg.worldStatePrefab = worldState;

            EditorSceneManager.SaveScene(scene, ScenePath);
        }

        private static void AddDefaultTransport(Component nm)
        {
            if (nm == null) return;
            // M18 双开实测修复（保留结论）：Fish-Net 4.x 的 Tugboat 类型全名是
            // 「命名空间 FishNet.Transporting.Tugboat」+「类名 Tugboat」= FishNet.Transporting.Tugboat.Tugboat。
            // 旧候选里 "…TugboatTransport" 不存在，而 "…Tugboat" 是**命名空间名**而非类型名，
            // 传进 Type.GetType 恒为 null —— 结果传输层从来没被添加，NetworkManager 实际不可用。
            //
            // M56：类型名不再写死在这个脚本里，改成向 NetTransport 要说明书。
            // 编辑器装配与运行时装配用的是**同一份**类型名，因此不可能出现
            // "编辑器装的是 A、运行时以为在跑 B"——那正是换传输层时最容易漏的一处。
            var spec = Net.NetTransport.Describe(Net.NetTransport.Default);
            var t = spec != null ? FindType(spec.TypeName) : null;
            if (t == null)
            {
                Debug.LogWarning("[StarPixel] 未找到传输层 " +
                                 (spec != null ? spec.TypeName : "<未知种类>") +
                                 "，请确认对应插件已安装，或手动为 NetworkManager 添加");
                return;
            }
            if (nm.GetComponent(t) == null) nm.gameObject.AddComponent(t);
        }

        /// <summary>
        /// 添加 Fish-Net PlayerSpawner 并指定玩家预制体：
        /// 客户端（含房主的本地客户端）加载完初始场景后，服务端自动 Spawn(nob, conn) 分配所有者。
        /// 这是 V4 推荐的玩家自动生成方式（NetworkManager 上不再有 PlayerPrefab 字段）。
        /// </summary>
        private static void AddPlayerSpawner(GameObject nmGo, GameObject player)
        {
            var psType = FindType("FishNet.Component.Spawning.PlayerSpawner");
            if (psType == null)
            {
                Debug.LogWarning("[StarPixel] 未找到 PlayerSpawner，请在 NetworkManager 上手动添加并指定玩家预制体");
                return;
            }
            var ps = nmGo.GetComponent(psType);
            if (ps == null) ps = nmGo.AddComponent(psType);

            var noType = FindType("FishNet.Object.NetworkObject");
            var nob = player != null && noType != null ? player.GetComponent(noType) : null;
            if (nob == null)
            {
                Debug.LogWarning("[StarPixel] 玩家预制体缺少 NetworkObject，PlayerSpawner 无法配置");
                return;
            }
            var setter = psType.GetMethod("SetPlayerPrefab", new[] { noType });
            if (setter != null) setter.Invoke(ps, new[] { nob });
            else Debug.LogWarning("[StarPixel] PlayerSpawner 未提供 SetPlayerPrefab，请在检视面板手动指定玩家预制体");
        }

        /// <summary>
        /// M18 双开实测修复（根因之一，最关键）：Fish-Net 官方预制体集合生成器。
        ///
        /// 此前本脚本只用反射调 <c>AddObject</c> 把 7 个预制体塞进 DefaultPrefabObjects，
        /// 但**没有设置 AssetPathHash、也没有 SetDirty/SaveAssets**——结果内存里加好了、
        /// 磁盘上的资产仍是 <c>_prefabs: []</c>（实测只有 424 字节的空文件）。
        /// 后果：服务端 Spawn 时 NetworkObject.PrefabId 恒为 65535（未分配），
        /// 客户端按 PrefabId 解析直接报「PrefabId 65535 is out of range」，
        /// 同时 NetworkObject 因从未被初始化而在 Awake 里提前 return，
        /// NetworkBehaviour 拿不到 NetworkObject 引用，任何 SyncVar 读写都 NullReferenceException
        /// （实测 60 秒刷 213MB / 5.7 万条异常，敌人一个都生成不出来）。
        ///
        /// Fish-Net 自己的 <c>Generator.GenerateFull</c> 会做全工程扫描、
        /// 设置 AssetPathHash 并落盘，这才是官方推荐路径；本方法反射调用它（internal 类型）。
        /// </summary>
        private static object TryGenerateDefaultPrefabs()
        {
            var genType = FindType("FishNet.Editing.PrefabCollectionGenerator.Generator");
            if (genType == null) return null;

            try
            {
                var gen = genType.GetMethod("GenerateFull",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (gen != null)
                {
                    // (settings = null → 用配置), forced = true（无视 Enabled 开关）, initializeAdded = true
                    gen.Invoke(null, new object[] { null, true, true });
                }

                var getter = genType.GetMethod("GetDefaultPrefabObjects",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static);
                if (getter != null)
                    return getter.Invoke(null, new object[] { null });
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[StarPixel] 官方预制体生成器调用失败，回退手动注册：" + e.Message);
            }
            return null;
        }

        private static void RegisterSpawnables(object nm, params GameObject[] prefabs)
        {
            if (nm == null) return;
            var noType = FindType("FishNet.Object.NetworkObject");
            if (noType == null)
            {
                Debug.LogWarning("[StarPixel] 未找到 FishNet NetworkObject，跳过预制体注册");
                return;
            }

            var nmType = nm.GetType();
            var prop = nmType.GetProperty("SpawnablePrefabs");

            // 优先走 Fish-Net 官方生成器：它会扫描全工程、设置 AssetPathHash 并落盘。
            // 手动 AddObject 无法设置 AssetPathHash，客户端解析生成包会失败，故只作回退。
            var generated = TryGenerateDefaultPrefabs();
            if (generated != null && prop != null && prop.CanWrite)
            {
                prop.SetValue(nm, generated);
                var cntProp = generated.GetType().GetProperty("Prefabs");
                int n = 0;
                if (cntProp != null)
                {
                    var list = cntProp.GetValue(generated, null) as System.Collections.IEnumerable;
                    if (list != null)
                        foreach (var _ in list) n++;
                }
                Debug.Log("[StarPixel] 已用官方生成器写入 SpawnablePrefabs，预制体 " + n + " 个");
                if (n > 0)
                {
                    AssetDatabase.SaveAssets();
                    return;
                }
                Debug.LogWarning("[StarPixel] 官方生成器得到 0 个预制体，回退手动注册");
            }

            if (prop == null)
            {
                Debug.LogWarning("[StarPixel] 未找到 SpawnablePrefabs，请在 NetworkManager 检视面板手动注册预制体");
                return;
            }

            object spawnables = prop.GetValue(nm, null);
            if (spawnables == null)
            {
                // V4：SpawnablePrefabs 为空时创建 DefaultPrefabObjects 资产并挂上，
                // 否则客户端无法按 PrefabId 解析生成包里的预制体（对象在客户端不存在）。
                var dpoType = FindType("FishNet.Managing.Object.DefaultPrefabObjects");
                if (dpoType == null || !prop.CanWrite)
                {
                    Debug.LogWarning("[StarPixel] SpawnablePrefabs 为空且无法自动创建，请在 NetworkManager 检视面板指定 DefaultPrefabObjects");
                    return;
                }
                string assetPath = "Assets/Net/DefaultPrefabObjects.asset";
                var existing = AssetDatabase.LoadAssetAtPath(assetPath, dpoType);
                if (existing != null)
                {
                    spawnables = existing;
                }
                else
                {
                    var so = ScriptableObject.CreateInstance(dpoType);
                    AssetDatabase.CreateAsset(so, assetPath);
                    spawnables = so;
                }
                prop.SetValue(nm, spawnables);
                Debug.Log("[StarPixel] 已创建 SpawnablePrefabs 资产：" + assetPath);
            }

            // V4：PrefabObjects.AddObject(NetworkObject, bool checkForDuplicates = false, bool initializeAdded = true)
            var addMethod = spawnables.GetType().GetMethod("AddObject", new[] { noType, typeof(bool), typeof(bool) })
                            ?? spawnables.GetType().GetMethod("AddObject", new[] { noType });
            if (addMethod == null)
            {
                Debug.LogWarning("[StarPixel] SpawnablePrefabs 未提供 AddObject(NetworkObject)，请手动注册预制体");
                return;
            }
            int added = 0;
            foreach (var p in prefabs)
            {
                if (p == null) continue;
                var nob = p.GetComponent(noType);
                if (nob == null) continue;
                var args = addMethod.GetParameters().Length == 3
                    ? new object[] { nob, false, true }
                    : new object[] { nob };
                addMethod.Invoke(spawnables, args);
                added++;
            }

            // M18 修复：AddObject 只改动内存实例，不 SetDirty 就不会写回磁盘，
            // 构建拿到的仍是空集合（正是此前双开实测失败的根因）。必须显式落盘。
            var prefabCollection = spawnables as ScriptableObject;
            if (prefabCollection != null)
            {
                EditorUtility.SetDirty(prefabCollection);
                AssetDatabase.SaveAssets();
            }
            Debug.Log("[StarPixel] 已回退手动注册 " + added + " 个可生成预制体（已落盘）");
        }

        // —— 工具 ——

        private static GameObject SavePrefab(GameObject go, string name)
        {
            string path = Path.Combine(PrefabDir, name + ".prefab").Replace("\\", "/");
            var saved = PrefabUtility.SaveAsPrefabAsset(go, path);
            UnityEngine.Object.DestroyImmediate(go);
            return saved;
        }

        private static Type FindType(string fullName)
        {
            var t = Type.GetType(fullName + ", Assembly-CSharp");     // 项目内脚本
            if (t != null) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(fullName);
                if (t != null) return t;
            }
            return null;
        }
    }
}
#endif
