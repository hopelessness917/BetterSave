# 更好的存档 / BetterSave

缺氧（Oxygen Not Included）存档性能优化模组。把后期自动存档的**主线程卡顿从约 3.6 秒压到约 0.54 秒（−85%）**，
而且**存档文件与原版逐字节一致**——兼容原版、可随时卸载、不会让存档读不出来。

> A save-performance mod for Oxygen Not Included. Main-thread save stutter on a 104 MB colony:
> **~3.6 s → ~0.54 s (−85%)**, with **byte-identical save output** to vanilla.

---

## 实测成绩

环境：i9-14900K，全成就2 存档，104 MB 未压缩，80 分钟连续游玩 53 次存档。

| | ms/MB | 104 MB 主线程窗口 |
|---|---|---|
| 原版 | ~35 | ~3.6 s |
| 本模组 | **5.4** | **529–583 ms（中位 541 ms）** |

其他收益：

| 项目 | 原版 | 本模组 |
|---|---|---|
| 缩略图抓取 | 每次存档一次（≥1.1 s） | **每局仅 1 次**，之后复制上一周期的图 |
| 托管垃圾回收 | 约每 50 秒一次，每次 0.5–0.6 s | **每 10 分钟一次**，每次 0.49 s |
| 存档缓冲 | 每次分配 ~110 MB | 复用池，53 次存档只分配 1 次 |

代价：**额外占用约 1.1 GB 内存**（10 分钟窗口内的累积），游戏自身的堆地板另有约 1.1 GB/小时的自身增长。

## 优化方法

| 源文件 | 做什么 |
|---|---|
| `SavePatch` | 把 `SaveLoader.Save` 里的缓冲分配换成复用池；把压缩与落盘交给后台线程 |
| `SaveBuffer` + `PooledStream` | 170 MB 缓冲池，带滞回扩容；`PooledStream` 是自定义 `MemoryStream`，写路径按 `MemoryStream` 的写法内联容量判断 |
| `Sink` | 后台压缩（zlib）+ 落盘；**先写临时文件再 `File.Move`**，硬杀进程不会破坏旧档 |
| `GcModeGate` | 整周期把托管回收置为 `Disabled`，只在"距上次回收满 10 分钟"或异常时放行 |
| `SerializerPatch` + `FieldPlan` | 用编译委托替换 `SerializationTemplate.SerializeData` 里的逐字段反射；每个字段首次使用跑"快写 vs 参考实现"两条路径并逐字节比较 |
| `IsDefinedPatch` | 缓存 `SkipSaveFileSerialization` 判定，免掉每组件一次反射 |
| `SaveTransform` | 重写 `SaveLoadRoot.SaveWithoutTransform`：**按类型缓存类型名的 UTF8 字节**、合并两趟遍历、按类型缓存跳写标记、长度字段直接回填缓冲 |
| `ThumbnailAsync` | 缩略图抓取后的 PNG 编码与写盘搬后台；第二次起直接复制上一周期的图 |
| `FrameWatch` / `GcTuner` | 诊断：体感窗口、卡顿日志、堆采样 |

单项贡献（会话内交替 A/B 实测）：

| 改动 | 收益 |
|---|---|
| 类型名缓存 UTF8 字节（跳过 `WriteKleiString` 的两次编码，61.7 万次/存档） | **−245 ms** |
| 重写 `SaveWithoutTransform` 合计 | **−250 ms（−31%）** |
| `SerializeData` 热路径三处减法（FieldPlan 数组缓存、去计数器、绕开 Harmony 跳板） | −43–48 ms（−5%） |
| 跳过存档后的 `GC.Collect()` | 约 −1100 ms，但回收只是推迟到 1–2 秒后（520–610 ms），**净省约 550 ms** |

## 编译与构建

### 前置条件

| 需要 | 说明 |
|---|---|
| **VS 自带的 MSBuild** | 老式 csproj + .NET Framework 4.8，**`dotnet msbuild` 不行** |
| **缺氧本体** | 用于引用游戏程序集 |
| **[官方 PLib 4.25](https://github.com/peterhaneve/ONIMods/releases/tag/PLib4.25)** | 下载 `PLib.dll` 放进 `更好的存档\libs\`（MIT，许可证见 `libs\PLib.LICENSE.txt`） |
| ILRepack 2.0.48 | NuGet 自动还原，无需手动 |

### 配置本地路径

复制 `更好的存档\Directory.Build.props.default` 为 **`Directory.Build.props.user`**（已在 `.gitignore` 里），
再把 `GameLibsFolder` / `ModFolder` 改成你自己机器上的路径。两个文件二选一加载：
`.user` 存在就用 `.user`，否则用 `.default`。`ModFolder` 留空 = 只编译不部署。

### 编译

```bat
msbuild 更好的存档\更好的存档.csproj /t:Restore
msbuild 更好的存档\更好的存档.csproj /p:Configuration=Release
```

`/t:Restore` 只需第一次（还原 ILRepack）。

产物与自动部署：

```
bin\Release\更好的存档.dll            未合并（仅编译产物）
bin\Release\merged\更好的存档.dll     ILRepack 合并 + PLib 内部化后的最终 DLL
        ↓ 构建自动复制
<ModFolder>\BetterSave.dll            重命名为游戏认的文件名
<ModFolder>\mod.yaml  mod_info.yaml   构建时生成（不要手工改）
<ModFolder>\translations\*.po         选项面板的中英文案
```

### 版本号

**只改 csproj 里的 `<ModVersion>` 一处**，构建会自动同步到：

- 程序集 `AssemblyFileVersion`（选项面板显示的「模组版本」）
- `mod_info.yaml` 的 `version`

### 构建时进行检查

| 检查 | 行为 |
|---|---|
| 找不到 `libs\PLib.dll` | **报错**并给出下载地址 |
| 找不到 `translations\*.po` | **报错**——否则选项面板会显示 `STRINGS.*` 原始键名 |

### 测试错误（会坏档）

1.  **`Manager.GetSerializationTemplate()` 有注册副作用。**
   它不是纯查找——它会把类型登记进序列化目录，而 `Manager.SerializeDirectory()`
   写出的类型目录就靠这张表。**任何「省掉这次调用」的优化都会让存档的类型目录不完整、
   读档时报 `no such class exists`。** 另注意 `Manager.Clear()` 在每次存档开头都会清空这张表。

## 安全设计

1. **零成本回退**：`SaveTransform` 用 Harmony **prefix 返回 `true`/`false`** 控制是否跳过原版；
   "回退"就是返回 `true` 让原版照跑——不需要 transpiler，也不需要 `HarmonyReversePatch`。
2. **每局前 2 次存档逐字节校验**：替换版写进独立 scratch 流，**真实数据始终由原版写出**，
   postfix 拿真实缓冲逐字节比对。所以**校验期结构上不可能损坏存档**。
   每次启动游戏都重新校验，游戏更新或新模组会在下次启动被自动重新验证。
3. **接管后兜底**：替换版抛异常 → 截断回对象起点 → 原版接手写完这次 → 永久停用。
4. **字段级快写**：每个字段首次使用做逐字节双路比对，不一致就永久回退该字段。
5. **反射名解析失败 → 直接不挂载**并报错。
6. **存档格式逐字节不变** ⇒ 原版兼容、随时可卸载、保证存档安全。

## 关键点

仅供参考：

**存档管线的确切结构**

`SaveLoader.Save(BinaryWriter)` 只有 21 条指令，**整条主线程窗口就是这 7 段**：

```
IOHelper.WriteKleiString(w, "world")
PrepSaveFile()                    // 很轻：建 SaveFileRoot + Grid.Damage 转字节 + 相机存档
Serializer.Serialize(prepSaveFile, w)
Game.SaveSettings(w)
Sim.Save(w, 0, 0)
saveManager.Save(w)               // 对象区，占未压缩字节 ~90%
Game.Instance.Save(w)
```

外层 `SaveLoader.Save(string, bool, bool)` 另外做：建缓冲 → 写文件头 → `Manager.SerializeDirectory`
→ `CompressContents` → `Timelapser.SaveColonyPreview` → `GC.Collect()`。

**经过测试发现的问题**

- **`SaveLoadRoot.SaveWithoutTransform` 是可重入的**：`ISaveLoadableDetails.Serialize` 的实现
  （如 `SolidConduitSerializer`）会再调 `SaveLoadRoot.Save`。实测最大深度 3。给它加任何状态都要做深度守卫。
- **它是 `GetComponents<KMonoBehaviour>()`，不是 `GetComponents<Component>()`**。
  dnlib 的 `MethodSpec` 默认不打印泛型实参，很容易误读——读 IL 时务必把泛型实参打出来。
- **`IOHelper.WriteKleiString` 每次调用做两次 UTF8 编码**（`GetByteCount` + `GetBytes`）。
  格式是 `[int32 长度][UTF8 字节]`，`null` 写 `-1`，长度 ≥1024 时走无缓冲分支并打警告。
- **Unity 的 icall 方法 Harmony 打不上**（`Component.GetComponents<T>()` 抛 `NotSupportedException`）。
  这条直接堵死了"把对象序列化搬到工作线程"的路。
- **Harmony 的 `CodeInstruction` 不能当分支操作数**（`Unexpected unemittable operand type`）。
  
**一位大佬建议测试**

- `GC.Collect(1)` 与 `GC.Collect()` **完全等价**：增量 1/1/1、堆降 64/64 MB、耗时 8–9 ms。
  `CollectionCount(0/1/2)` 全程恒等——**这一作的 Mono 在编译期就关掉了分代**。
- `GarbageCollector.Mode.Manual` 在非增量 Boehm 上是 **no-op**；`Mode.Disabled` 才真正停掉托管回收。
- Boehm 的触发是**分配驱动**（约每 250 MB 一次），不是堆驱动。
- 缺氧自身**没有设任何内存阈值**：`Assembly-CSharp` 里 0 处 `GC.GetTotalMemory`，
  6 处 `GC.Collect()` 全是事件驱动。

**性能测量方法（比较靠谱）**

- **跨会话基线不可靠，会话内交替 A/B 才可信。** 同一局内逐次存档交替开关、配对比较，
  能免疫 JIT、堆状态、殖民地增长带来的漂移。
- **只优化"单次 100 ns 以上"的每次调用开销**（编码、格式化、装箱、反射、真实字典查找、Harmony 跳板）。
  数 10 ns 以下的微操作意义不大  

## 主要问题
- **单线程**：存档时间 ∝ **CPU 单核性能**，多核无用（Unity icall 只能在主线程），基本无解。
- **硬盘速度不影响卡顿**，只影响后台压缩落盘与退出时的 flush。
- 每局**前 2 次存档是完整校验**（约 1.3–1.7 s），第 3 次起才是 ~0.54 s，在考虑是否改成4次校验确保安全。
- **额外占用约 1.1 GB 内存**，低内存机器需要留意，猜测目前8g以下应该可能出现，但无法求证。
- 只优化存档，不优化读档，是否要把读档也优化呢？？。
- 猜测剩余优化空间约 4–5%：剩余预算里 32% 是游戏自带的组件自定义序列化代码（`ISaveLoadableDetails.Serialize`），
  33% 是字段派发（已是"数组读 + 委托调用 + 写"三步），而且剩余优化复杂而且不一定有效，
  剩下600ms基本感觉都是游戏不可动的消耗，但是游戏新存档前期可以基本实现无感保存，可以探测一下原因，看是否与存档大小挂钩。


# 读档优化记录

## 读档优化放弃

在分析读档耗时后，决定暂时放弃进一步优化。主要耗时分布如下：

| 阶段 | 耗时 | 占比 | 是否可优化 | 说明 |
|:---|---:|---:|:---:|:---|
| Unity 对象实例化 + `SetActive(true)` | 8,072 ms | 62.9% | ❌❌ | Unity 引擎侧，主线程，动不了 |
| `LoadInternal`（托管反序列化） | 2,499 ms | 19.5% | ✅ | 我们的 O(N×M) 扫描 + 字符串分配在这里 |
| 整档解压（主线程） | 386 ms | 3.0% | ✅ | 可移到后台线程（同存档方案） |
| `SaveManager.Load` 自身 | 787 ms | 6.1% | ⚠️ 部分 | — |
| `Load(IReader)` 自身（Game/Grid 等） | 700 ms | 5.5% | ⚠️ 部分 | — |
| 文件读取 + header | 223 ms | 1.7% | ❌ | — |
| `DeserializeDirectory` | 153 ms | 1.2% | ✅ | 小 |
| `Sim.LoadWorld` | 13 ms | 0.1% | ❌ | 不用管（几乎不耗时 ✅） |

### 结论

- 主要瓶颈是 **Unity 对象实例化 + `SetActive(true)`**，耗时 **8,072 ms**，占比 **62.9%**，属于 Unity 引擎侧主线程，暂时无法直接优化。
- 真正可优化的重点是 **`LoadInternal`（托管反序列化）**，耗时 **2,499 ms**，占比 **19.5%**，主要问题在 O(N×M) 扫描和字符串分配。
- **整档解压** 可移到后台线程，但收益约 **3.0%**，感觉意义不大。
- `Sim.LoadWorld` 仅 **13 ms**，占比 **0.1%**，无需处理。
- 总耗时约 **12,833 ms**（约 **12.8 s**）。
- 总结纯主线程做，还不能拆分出来
