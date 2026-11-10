# URTypeRefGen

从 il2cppdumper / Il2CppInspectorRedux 产出的 **dummy dll** 生成 URKit 用的
`TypeRef` 常量和带类型访问器的 C++ 头文件。

解决的问题：URKit 的 SDK 是纯运行时按名字查找的，游戏自己的类型没有
任何编译期类型信息，成员名写错了要到运行时才报错。这个工具把 dump 里已经确认过
的 名字/成员 一次性落成头文件，于是有补全、有编译期检查、重命名只改一处。

**它不读 `global-metadata.dat`。** 输入是 dumper 已经解出来的 dummy 程序集（只有
元数据，没有 IL），所以对“元数据被加密”的目标同样适用——只要你手上有一个能用的
dump。

## 构建

```powershell
dotnet build URTypeRefGen.sln -c Release
```

## 用法

```powershell
# 1. 看看 dump 里有什么
.\TypeRefGen.exe --dll-dir .\DummyDllTest --list

# 2. 看某个类型的成员（挑要导出哪些）
.\TypeRefGen.exe --dll-dir .\DummyDllTest --type Draw --list-members

# 3. 生成头文件
.\TypeRefGen.exe
  --dll-dir .\DummyDllTest `
  --namespace MyTrainer `
  --type AdvGameState --type Draw --type Item `
  --methods `
  --report .\report.txt `
  --out ..\mod\game\game_types.h

# 4. 用 glob 批量选
.\TypeRefGen.exe --dll-dir .\DummyDllTest --match "*Alien*" --exclude "*<>*"
```

`--list` / `--list-members` / `--help` 有完整说明。

## 生成出来的东西

```cpp
inline constexpr TypeRef kDraw{"Assembly-CSharp", "", "Draw"};

struct Draw {
    static constexpr TypeRef type() { return kDraw; }

    Object o;
    Draw() = default;
    explicit Draw(Object value) : o(value) {}
    explicit operator bool() const { return static_cast<bool>(o); }
    void *handle() const { return o.handle(); }

    // field: int score
    std::int32_t score() const { return o.GetField<std::int32_t>("score"); }
    void set_score(std::int32_t value) { o.SetField<std::int32_t>("score", value); }

    // property: int notebook_pos (property)
    std::int32_t notebook_pos() const { return o.GetProperty<std::int32_t>("notebook_pos"); }

    // method: void ShowText(string)
    void ShowText(std::string_view arg0) const { return o.Call<void>("ShowText", arg0); }
};
```

- **类型**：`TypeRef` 的 image / namespace / class 三元组，取自程序集自身的元数据。
- **字段 / 属性 / 方法**：只导出能安全 marshal 的成员。
- **静态成员**：`StaticGetField` / `InvokeStatic`。
- **`Find()` / `FindAll()`**：仅对继承自 `UnityEngine.Object` 的类型生成。
- **`New(...)`**：每个 public 构造一个，内部是 `Object::New(type)` 分配 + `CallExact(".ctor", 参数类型名)`。
  `UnityEngine.Object` 的派生类**不生成**——它们必须走 `Instantiate()` / `AddComponent()`，
  裸分配只会得到一个没有原生对象的托管空壳。参数类型无法映射（数组、`List<T>`、自定义结构体）
  的构造跳过并写进 report。
- **枚举**：`enum class`，值来自元数据；跨命名空间的枚举扁平化成 `Ns_Type`。
- **自检块**：`#if defined(URTYPEGEN_SELF_CHECK)` 里实例化每个访问器。单独编译一个
  包含该头文件的 TU 并定义这个宏，就能证明整个头文件可编译，而不是等用到才炸。
- **重载**：`Call<Ret>` 只按参数个数匹配。同名同参数个数的重载默认**跳过并写进
  report**；加 `--exact-methods` 改生成 `CallExact`，用 dump 里的
  参数类型名精确匹配。静态方法的同名同个数重载没有对应 API，始终跳过并报告。

## 安全边界（重要）

工具**只导出能确定映射的成员**，其余写进 report，不会瞎猜：

| C# 类型 | 处理 |
|---|---|
| 基元类型 / `string` / `object` | 直接映射 |
| `UnityEngine.Vector2/3/4`、`Quaternion`、`Color`、`Rect`、`Bounds` | 映射到 SDK 的 POD 镜像 |
| 枚举 | 生成 `enum class`，否则退化成底层整数 |
| 其它 class / interface | `Object`（托管引用） |
| `List<T>`、`Dictionary<,>`、数组、泛型 | **跳过** + report |
| 自定义 struct / 值类型 | **跳过**（布局无法假设），只保留 `TypeRef` |
| 嵌套类型 | 默认跳过——URKit 的 `FindClass` 走 `il2cpp_class_from_name`，不解析嵌套 |
| 泛型开放类型 | 默认跳过 |
| `string` 字段的 setter | 跳过：`SetField<std::string>` 没有对应的托管写回路径 |

另外这些是**运行时**才成立的假设，工具无法验证：

- 类型/成员名必须和运行时元数据一致。混淆过的游戏照样能用，
  因为工具复制的是 dump 里的真实名字。
- 游戏更新后成员可能改名或消失，重新生成并重编即可暴露问题。
- `Call<Ret>` 按 **参数个数** 解析重载。同名同参数个数的重载需要手工改成
  `CallExact` 并给出参数类型名。
- 生成的 wrapper 用 `Object` 承载句柄。对非 `UnityEngine.Object` 的普通类，
  不要调 `alive()` 之类的 Unity 专有路径。
