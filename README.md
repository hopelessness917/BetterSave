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

## 安全设计

1. **零成本回退**：`SaveTransform` 用 Harmony **prefix 返回 `true`/`false`** 控制是否跳过原版；
   "回退"就是返回 `true` 让原版照跑——不需要 transpiler，也不需要 `HarmonyReversePatch`。
2. **每局前 2 次存档逐字节校验**：替换版写进独立 scratch 流，**真实数据始终由原版写出**，
   postfix 拿真实缓冲逐字节比对。所以**校验期结构上不可能损坏存档**。
   每次启动游戏都重新校验，游戏更新或新模组会在下次启动被自动重新验证。
3. **接管后兜底**：替换版抛异常 → 截断回对象起点 → 原版接手写完这次 → 永久停用。
4. **字段级快写**：每个字段首次使用做逐字节双路比对，不一致就永久回退该字段。
5. **反射名解析失败 → 直接不挂载**并报错。
6. **存档格式逐字节不变** ⇒ 原版兼容、随时可卸载、不会读不了档。

## 本仓库里值得参考的缺氧技术点

这些是开发过程中从 IL 和实测里挖出来的，写在这里方便参考：

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

**几个容易踩的坑**

- **`SaveLoadRoot.SaveWithoutTransform` 是可重入的**：`ISaveLoadableDetails.Serialize` 的实现
  （如 `SolidConduitSerializer`）会再调 `SaveLoadRoot.Save`。实测最大深度 3。给它加任何状态都要做深度守卫。
- **它是 `GetComponents<KMonoBehaviour>()`，不是 `GetComponents<Component>()`**。
  dnlib 的 `MethodSpec` 默认不打印泛型实参，很容易误读——读 IL 时务必把泛型实参打出来。
- **`IOHelper.WriteKleiString` 每次调用做两次 UTF8 编码**（`GetByteCount` + `GetBytes`）。
  格式是 `[int32 长度][UTF8 字节]`，`null` 写 `-1`，长度 ≥1024 时走无缓冲分支并打警告。
- **Unity 的 icall 方法 Harmony 打不上**（`Component.GetComponents<T>()` 抛 `NotSupportedException`）。
  这条直接堵死了"把对象序列化搬到工作线程"的路。
- **Harmony 的 `CodeInstruction` 不能当分支操作数**（`Unexpected unemittable operand type`）。
  要"跳过原方法"应该用 **prefix 返回 `false`**，不要用 transpiler 去造分支。
- **Harmony finalizer 在正常路径上也会执行**（生成成 `finally`），判据要用 `__exception != null`。
- **收尾钩子（如 `Game.OnApplicationQuit`）要最先安装**：一个可选的补丁组件挂载失败会连带让它装不上。

**Boehm GC（Mono）**

- `GC.Collect(1)` 与 `GC.Collect()` **完全等价**：增量 1/1/1、堆降 64/64 MB、耗时 8–9 ms。
  `CollectionCount(0/1/2)` 全程恒等——**这一作的 Mono 在编译期就关掉了分代**。
- `GarbageCollector.Mode.Manual` 在非增量 Boehm 上是 **no-op**；`Mode.Disabled` 才真正停掉托管回收。
- Boehm 的触发是**分配驱动**（约每 250 MB 一次），不是堆驱动。
- 缺氧自身**没有设任何内存阈值**：`Assembly-CSharp` 里 0 处 `GC.GetTotalMemory`，
  6 处 `GC.Collect()` 全是事件驱动。

**性能测量方法（这个项目最贵的教训）**

- **跨会话基线不可靠，会话内交替 A/B 才可信。** 同一局内逐次存档交替开关、配对比较，
  能免疫 JIT、堆状态、殖民地增长带来的漂移。这个项目里有两次改动因为用跨会话数字对比而
  被误判为"零收益"。
- **只优化"单次 100 ns 以上"的每次调用开销**（编码、格式化、装箱、反射、真实字典查找、Harmony 跳板）。
  数 10 ns 以下的微操作是浪费时间——单价会被高估一个数量级，还常被新增的开销抵掉。
  本项目三次估算的准确率：单次 400 ns 的编码（估 124–247 → 实 245，准）；
  单次 2–3 ns 的微操作（估 165 → 实 10，高 16 倍）；单次 5–20 ns 的三处减法（估 175 → 实 43，高 4 倍）。

## 构建

需要：

- 缺氧 **U59-744825** 或更新（默认 Steam 安装位置）
- **Visual Studio 的 MSBuild**（目标 .NET Framework 4.8）

```bat
msbuild 更好的存档\更好的存档.csproj /p:Configuration=Release
```

游戏不在默认位置时，覆盖 `ONIManagedDir`：

```bat
msbuild 更好的存档\更好的存档.csproj /p:Configuration=Release ^
  /p:ONIManagedDir="D:\Steam\steamapps\common\OxygenNotIncluded\OxygenNotIncluded_Data\Managed"
```

**注意**：请用 VS 的 `MSBuild.exe`，不要用 `dotnet msbuild`——`dotnet` 产出的 DLL 游戏加载不了。
`0Harmony.dll` 与全部游戏程序集都从 `$(ONIManagedDir)` 引用，仓库里不含任何二进制。

## 打包与部署

把 `bin\Release\更好的存档.dll` 复制到 `mods\Local\<模组目录名>\`，重命名成任意名字
（ONI 会加载目录里的所有 DLL，本项目部署时用的是 `BetterSave.dll`），并放上元数据：

```yaml
# mod.yaml —— staticID 必须与创意工坊一致，否则会被当成另一个模组
staticID: BetterSave
description: "..."
title: "更好的存档"
```

```yaml
# mod_info.yaml
APIVersion: 2
minimumSupportedBuild: 719533
version: v2.9.0
supportedContent: ALL
```

## 代码结构

```
更好的存档/
├── 更好的存档.slnx
└── 更好的存档/
    ├── 更好的存档.csproj          # 老式工程：新增 .cs 必须登记进 <Compile Include>
    ├── Properties/AssemblyInfo.cs
    ├── tools/MeasurePatch.cs.txt  # 一次性测量脚本，后缀是 .txt 所以不参与编译
    └── src/
        ├── ModLoader.cs           # 入口（KMod.UserMod2 子类），逐个挂载各组件，每个组件独立 try/catch
        ├── SavePatch.cs           # SaveLoader.Save 的 transpiler（换缓冲）+ prefix/postfix + GC 抑制
        ├── SaveTransform.cs       # 重写 SaveWithoutTransform + 逐字节校验 + 可重入守卫
        ├── SerializerPatch.cs     # SerializeData 的编译委托替换 + 双路校验
        ├── FieldPlan.cs           # 逐字段的编译快写器
        ├── IsDefinedPatch.cs      # IsDefined 缓存
        ├── PooledStream.cs        # 自定义 MemoryStream（池化缓冲）
        ├── SaveBuffer.cs          # 缓冲池
        ├── Sink.cs                # 后台压缩 + 落盘
        ├── GcModeGate.cs          # 托管回收门控
        ├── GcTuner.cs             # 堆采样
        ├── FrameWatch.cs          # 体感窗口监控
        └── ThumbnailAsync.cs      # 缩略图后台化
```

## 已知边界

- **单线程**：存档时间 ∝ **CPU 单核性能**，多核无用（Unity icall 只能在主线程）。
- **硬盘速度不影响卡顿**，只影响后台压缩落盘与退出时的 flush。
- 每局**前 2 次存档是完整校验**（约 1.3–1.7 s），第 3 次起才是 ~0.54 s。
- **额外占用约 1.1 GB 内存**，低内存机器需要留意。
- 只优化存档，不优化读档。
- 剩余优化空间约 4–5%：剩余预算里 32% 是游戏自带的组件自定义序列化代码（`ISaveLoadableDetails.Serialize`），
  33% 是字段派发（已是"数组读 + 委托调用 + 写"三步）。

## 许可

[MIT](LICENSE) © 2026 hopelessness917

欢迎参考、修改、再发布。如果这个项目里的某段实现或某条实测结论帮到了你，在 README 里提一句就好。
