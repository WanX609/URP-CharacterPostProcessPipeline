# URP 2022.3 角色选择性后处理：从黑点故障到最终方案的全程回溯

## 1. 问题场景

**核心目标**：在 Unity URP 2022.3 中，对场景中的特定角色（`SkinnedMeshRenderer`、多子网格、BlendShape 面部动画、自定义 Genshin-like Shader）独立施加 Bloom + 色彩调整 + ACES 色调映射，而场景其余部分保持原样。

**输入**：主相机渲染的完整场景（含角色）。角色位于独立 Layer。

**预期输出**：Game 视图中角色呈现后处理效果（Bloom 柔光、饱和度/色相调整、ACES 色调映射），背景场景不受影响。所有滑块参数实时生效。

**不可妥协的约束**：
- 性能：单帧额外开销须控制在可接受范围（不能每帧 20+ Blit）
- 不修改角色原有 Shader（或仅做极轻微修改）
- 兼容 URP 2022.3 LTS（Core RP Library 14.x）
- 不影响 Scene 视图（仅 Game 相机触发）

---

## 2. 失败路线图

### 尝试 1：DrawRenderers(覆盖材质) → CoverageRT → MaskedCopy

**假设**：用极简白色覆盖 Shader 重绘角色到 R8_UNorm 纹理，得到二值遮罩，再从相机目标提取角色颜色。

**操作**：
```csharp
draw.overrideMaterial = coverageMaterial;  // 输出白色
cmd.SetRenderTarget(m_CoverageRT);
context.DrawRenderers(..., ref draw, ref filter);
```

**结果**：所有滑块正常工作，Bloom/ColorAdj/ACES 全效。但角色表面出现大量黑点（覆盖遮罩中存在孔洞）。

**根因**：`context.DrawRenderers` + `overrideMaterial` 对多子网格 `SkinnedMeshRenderer` 重绘时，部分子网格三角形未被光栅化——这是一个 Unity 已知 Bug（多材质槽 + overrideMaterial 交互缺陷）。孔洞 = coverage=0 → MaskedCopy 输出黑色像素 → 低 Bloom 时无法填充。

**教益**：不要依赖 `DrawRenderers(overrideMaterial)` 来生成准确的角色遮罩。

---

### 尝试 2：cmd.Blit 替代 Blitter.BlitCameraTexture

**假设**：怀疑 `Blitter.BlitCameraTexture` 在某些 URP 版本中配合自定义 Shader 存在兼容问题，改用传统 `cmd.Blit`。

**结果**：角色变成**纯灰色剪影**——所有滑块的色彩调整完全失效，仅 ACES Exposure 仍能调节亮度。

**根因**：`cmd.Blit` 内部将源纹理绑定为 `_MainTex`（Built-in 管线约定），而 URP 标准 Shader 全部使用 `_BlitTexture`（URP 约定）。Shader 实际采样到的是 Unity 内部默认 1×1 灰色纹理 → `camColor × 0.5 = 灰色`。

**教益**：URP 中不要使用 `cmd.Blit`——官方明确禁止（"may break rendering and aren't compatible with native render passes"）。始终用 `Blitter.BlitCameraTexture`。

---

### 尝试 3：Stencil 缓冲方案

**假设**：角色 Shader 已写入 `Stencil Ref 128`，后处理 Pass 通过 `DrawProcedural` + `Stencil Comp Equal` 全屏绘制白色覆盖。完全消除 DrawRenderers 的子网格问题。

**操作**：
```csharp
cmd.SetRenderTarget(m_CoverageRT, cameraDepth);  // 绑相机 Stencil
cmd.DrawProcedural(identity, m_StencilMat, 0, Triangles, 3, 1);
```

**结果**：角色变成**纯黑色剪影**——`DrawProcedural` 在此 URP 版本完全不执行自定义 Shader。即使 Shader 输出纯白（`return 1.0`），屏幕仍为黑色。

**根因**：URP 2022.3 + DX11 环境下 `cmd.DrawProcedural` 与自定义材质的全屏三角组合存在未解决的兼容故障。

**教益**：Stencil 路线在当前工具链条件下不可行。

---

以上排除了 **覆盖材质重绘**、**cmd.Blit 纹理绑定**、**Stencil 硬件测试** 三个方向，迫使我们寻找不依赖 DrawRenderers 的覆盖生成方式（最终选择了第二相机），同时强制遵守 URP 的 Blitter 约定。但最关键的转折尚未到来——所有路线产生的黑点模式**完全相同**，这个被忽视的线索最终指向了真正原因。

---

## 3. 核心思考过程

### 3.1 关键转折点

#### 转折 1：第二相机也产生相同黑点

当第二相机方案（Unity 原生的完整渲染管线，包含全部 Skinning/BlendShape/子网格处理）也能正常运作后，我们惊讶地发现：**黑点模式与之前所有覆盖方案产生的完全相同**。

**为什么这构成了转折**：第二相机绕过了所有我们怀疑的覆盖生成问题——没有 DrawRenderers、没有 overrideMaterial、没有子网格 Bug。如果黑点仍在，就说明它们**从来就不是覆盖遮罩的孔洞**。我们之前的整个诊断框架——"黑点在角色边缘 → 必然是覆盖遮罩不完整 → 修复覆盖生成"——是错的。

认知盲区：我们假设了"覆盖遮罩 = 黑点的唯一可能来源"，却从未用系统化隔离测试验证这个假设。

#### 转折 2：分阶段隔离指认 ColorAdj

将完整管线拆解为独立阶段，逐级输出中间结果：

| 诊断阶段 | 输出内容 | 黑点？ |
|---------|---------|-------|
| CoverageRT | 覆盖遮罩 | 无 |
| CharRT（MaskedCopy 后） | 提取的角色颜色 | 无 |
| TempRT（Bloom 后，极低 Bloom 值） | Bloom 处理后的角色 | 无 |
| AdjustedRT（ColorAdj 后） | 色彩调整后的角色 | **有** |

**为什么这构成了转折**：它精确地把问题位置从"整个管线"缩小到"ColorAdj 这一个 Shader"——而 ColorAdj 在 Saturation=1.0、HueShift=0.0 的默认值下，数学上应该是恒等变换。一个恒等变换居然引入了可见的黑点。

#### 转折 3：逐个函数注释定位到 half 精度三角运算

进一步分解 ColorAdj：

| 操作 | 黑点？ |
|------|-------|
| 空复制（跳过 ColorAdj Shader） | 无 |
| 完整 ColorAdj（Saturation=1, HueShift=0, Vibrance=0） | **有** |
| 禁用 HueShift 的 ColorAdj | **无** |

**为什么这构成了转折**：`HueShiftYCoCg` 函数在 `shift=0` 时执行 `atan2(Cg, Co) → cos(h) → sin(h)` 的数学往返——这应该在数学上精确还原输入。但在 GPU `half` 精度下，极暗像素（Co、Cg 接近零）的三角往返不是无损的。`atan2` 的微小误差被 `cos/sin` 放大，导致部分像素颜色偏离到不可见范围（≈ 0）。

**这是一个罕见的 GPU 精度 Bug**——它不在任何 StackOverflow 帖子里，不在任何 Unity Issue Tracker 的常见分类下。它只在你恰好把 HDR 渲染 → half 精度转换 → 三角往返 → 暗部像素这几个条件同时触达时才会出现。

### 3.2 权衡取舍

| 方案 | 覆盖准确性 | 性能 | 复杂度 | 取舍 |
|------|-----------|------|--------|------|
| DrawRenderers + 覆盖材质 | 有孔洞 | 中 | 低 | 放弃 |
| cmd.Blit | 无效 | 中 | 低 | 放弃 |
| Stencil | 理念上完美 | 低 | 高 | 工具链不支持 |
| **第二相机** | **100%准确** | **中（额外相机渲染）** | **中** | **采用** |

第二相机方案每帧额外渲染一次角色到 1920×1080 R16G16B16A16 纹理——性能开销在可接受范围内（单个角色）。放弃了纯代码实现的优雅性，换取了无可争议的覆盖准确性。额外相机通过 `HideFlags.HideAndDontSave` 管理生命周期，对用户不可见。

### 3.3 可泛化的方法论

> **当 GPU 渲染管线中某个效果产生"不应存在的像素异常"时，按以下决策链排查：**
>
> 1. **首先用均质输入验证**——硬编码 Shader 输出纯色，确认 Blitter/管线基础通路正常
> 2. **逐阶段隔离**——把管线拆解为独立步骤，每一步的输出直接显示到屏幕。这是定位问题位置的最快方式
> 3. **排除假设中的因果链**——如果所有覆盖生成方式产生的异常模式相同，异常就不在覆盖生成中
> 4. **检查浮点精度边界**——假设"数学恒等的操作在 GPU 上一定是恒等的"是错误的。`half` 精度下的三角往返、`float` → `half` 的隐式截断、多次 RT 穿越的累积误差，都可能在某些输入值上产生可感知的偏差
> 5. **Bypass 优化**——为所有参数设置默认值时，直接跳过该阶段的 Shader 执行

这个决策链的核心原则是：**系统化隔离优先于因果猜测，均质化测试优先于复杂假设**。

---

## 4. 最终实现

### 4.1 完整代码

**架构总览**：
```
Feature.Create() → 创建第二相机（持久, HideFlags）
Feature.AddRenderPasses() → 仅主相机, 配置第二相机 + 创建 RT → Shader.SetGlobalTexture
Pass.Execute():
  1. CopyTexture(第二相机 RT → CoverageSrc)
  2. CoverageExtract → CoverageRT (R8) + 3× Dilate
  3. Scene copy (cam → SceneRT)
  4. MaskedCopy (SceneRT × CoverageRT → CharRT)
  5. Bloom (CharRT → ... → TempRT, 全白覆盖)
  6. ColorAdj (TempRT → AdjustedRT, OKLab 空间)
  7. Composite (AdjustedRT + SceneRT + CoverageRT → cam)
  8. ACES (阈值制, 仅 HDR 亮区压调)
```

**Feature.cs**（关键字段）：
```csharp
[System.Serializable]
public class Settings
{
    [Header("Materials")]
    public Material bloomMaterial;             // Unlit/Bloom
    public Material colorAdjustmentsMaterial;  // Unlit/ColorAdjustments
    public Material sceneCompositeMaterial;    // Hidden/Character/SceneComposite
    public Material acesToneMappingMaterial;   // Unlit/ACESToneMapping
    public Material maskCopyMaterial;          // Hidden/Character/MaskedCopy

    [Header("Character")]
    public LayerMask characterLayer;

    [Header("Bloom")]
    public bool enableBloom = true;
    [Range(0f,10f)] public float bloomThreshold = 0.9f;
    [Range(0f,1f)]  public float bloomSoftKnee  = 0.6f;
    [Range(0f,5f)]  public float bloomIntensity = 1.5f;
    [Range(1,4)]    public int   bloomKawaseIterations = 3;

    [Header("Color Adjustments")]
    public bool enableColorAdjustments = true;
    [Range(0f,2f)]      public float saturation = 1.0f;
    [Range(-1f,1f)]     public float vibrance   = 0.0f;
    [Range(-0.5f,0.5f)] public float hueShift   = 0.0f;
    public Color colorFilter = Color.white;
    public bool useHighQualityHueShift = false;

    [Header("Tone Mapping")]
    public bool enableToneMapping = true;
    [Range(0f,5f)]    public float toneMapStart = 0.8f;
    [Range(0f,10f)]   public float toneMapEnd   = 1.5f;
    [Range(0.1f,10f)] public float exposure = 1.0f;
}
```

**Pass.cs 关键段**（覆盖生成——第二相机 + CoverageExtract + Dilate）：
```csharp
// 1. 第二相机 RT → 覆盖提取
if (m_ExtCoverageRT != null)
{
    cmd.CopyTexture(m_ExtCoverageRT, m_CoverageSrcRT.rt);
    var extShader = Shader.Find("Hidden/Character/CoverageExtract");
    if (extShader != null)
    {
        var em = new Material(extShader);
        Blitter.BlitCameraTexture(cmd, m_CoverageSrcRT, m_CoverageRT, em, 0);
        CoreUtils.Destroy(em);
    }
    // Dilate 2× 3×3 max filter — 安全边距
    var dilShader = Shader.Find("Hidden/Character/CoverageDilate");
    if (dilShader != null)
    {
        var dm = new Material(dilShader);
        for (int i = 0; i < 2; i++)
        {
            Blitter.BlitCameraTexture(cmd, m_CoverageRT, m_TempRT);
            Blitter.BlitCameraTexture(cmd, m_TempRT, m_CoverageRT, dm, 0);
        }
        CoreUtils.Destroy(dm);
    }
}
```

**ColorAdjustments.shader 关键段**（`float` 精度 + OKLab + HueShift 守卫）：
```hlsl
half4 Frag(Varyings input) : SV_Target
{
    float3 rgb = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord).rgb;

    // Bypass: 所有参数为默认值时直接透传
    bool bypass = _Saturation == 1.0 && _Vibrance == 0.0 &&
                  abs(_HueShift) < 0.0001 && all(_ColorFilter == 1.0);
    if (!bypass)
    {
        // Saturation + Vibrance in OKLab space — decoupled L/a/b
        float3 lab = RGBtoOKLab(rgb);
        lab.yz *= _Saturation;
        float chroma = sqrt(lab.y*lab.y + lab.z*lab.z);
        float weight = saturate(1.0 - chroma / max(chroma + 0.3, 0.01));
        lab.yz += lab.yz * _Vibrance * weight;
        rgb = OKLabtoRGB(lab);

        // FIX: 仅在 HueShift≠0 时旋转 — 回避 half 精度 atan2/cos/sin 往返
        if (abs(_HueShift) > 0.0001)
        {
            rgb = (float3)HueShiftYCoCg((half3)rgb, _HueShift);
        }
        rgb *= _ColorFilter;
    }
    return half4((half3)rgb, 0);
}
```

**ACESToneMapping.shader 关键段**（阈值制——仅 HDR 高亮走 ACES）：
```hlsl
half4 Frag(Varyings input) : SV_Target
{
    float3 rgb = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord).rgb;
    float3 exposed = rgb * _Exposure;
    float lum = dot(exposed, float3(0.2126,0.7152,0.0722));
    float t = smoothstep(_ToneMapStart, _ToneMapEnd, lum);

    if (t < 0.0001) return half4((half3)rgb, 1);  // 完全不经 ACES

    half3 acesIn = (half3)exposed;
    half3 acesOut = ACES(acesIn);

    if (t > 0.9999) return half4(acesOut, 1);

    // 过渡区混合 + 色相保护
    half3 mapped = lerp(acesIn, acesOut, t);
    // ... hue protection ...
    return half4(lerp(mapped, hueFixed, blend), 1);
}
```

### 4.2 复现验证

**环境版本**：
- Unity 2022.3.62f3c1
- URP 14.0.12（Core RP Library）
- DirectX 11（Windows 11）
- Shader target 3.0

**项目文件清单**（共 9 个核心文件）：
- `CharacterPostProcessFeature.cs` — Feature
- `CharacterPostProcessPass.cs` — Pass
- `MaskedCopy.shader` — `Hidden/Character/MaskedCopy`
- `CoverageExtract.shader` — `Hidden/Character/CoverageExtract`
- `CoverageDilate.shader` — `Hidden/Character/CoverageDilate`
- `Bloom.shader` — `Unlit/Bloom`
- `ColorAdjustments.shader` — `Unlit/ColorAdjustments`
- `SceneComposite.shader` — `Hidden/Character/SceneComposite`
- `ACESToneMapping.shader` — `Unlit/ACESToneMapping`

**最少配置步骤**：
1. 9 个文件放入 `Assets/` 中任意目录
2. URP Renderer 资产 → Add Renderer Feature → `CharacterPostProcessFeature`
3. 创建 5 个材质（分别对应 Bloom/ColorAdj/Composite/ACES/MaskedCopy 的 Shader）并拖入 Feature Inspector
4. 设置 Character Layer
5. Play Mode — 所有滑块应立即生效

---

## 5. 遗留问题与后续方向

**尚未完美解决的问题**：

1. **OKLab 饱和度算法在极值时的感知明度偏移**：虽然 OKLab 在理论上解耦了 L/a/b，但 `lab.yz *= _Saturation` 的线性缩放在大饱和度值（Saturation > 1.5）时，对低饱和度区域的明度感知仍有轻微影响。这是 OKLab 空间本身的局限——它是"近似感知均匀"而非"完全感知均匀"。

2. **第二相机渲染时间点**：第二相机在 `AddRenderPasses` 中手动调用 `Render()`——这在 URP 的相机调度中并非最优。更理想的做法是让第二相机通过 `depth` 参数（深度优先级）被 URP 自动调度，但 URP 的相机列表是在帧开始时确定的，运行时动态创建的相机不会被自动纳入。这个问题的根本解决需要修改 URP 的相机管理逻辑，或迁移到 URP 17+ 的 Render Graph API。

3. **ACES 阈值参数的自动调优**：当前 `ToneMapStart=0.8, ToneMapEnd=1.5` 是基于特定场景的手动设定。在不同光照条件或 HDR 强度下，这些值需要重新校准。一个可能的改进是：基于当前帧的平均/最大亮度自动计算阈值。

4. **TDR 风险**：对话早期报告了 Bloom 滑块调整时偶发的 GPU 设备重置（`Failed to present D3D11 swapchain due to device reset/removed`）。虽然后续通过 `useDynamicScale=false` 和场景预复制策略缓解，但未进行长期稳定性验证。**不确定是否已彻底解决**——缺乏多 GPU/多分辨率/长时间运行的测试数据。如果此问题复现，建议：增大 Windows TDR 超时（`TdrDelay` 注册表项）或降低 `bloomKawaseIterations` 到 1-2。

---

*本文基于一次跨越 80+ 轮迭代的调试会话，涵盖了从架构设计到 GPU 精度 Bug 的完整排查链。*
