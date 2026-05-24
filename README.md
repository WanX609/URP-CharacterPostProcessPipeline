# URP Character Selective Post-Processing Pipeline

Unity 2022.3 URP 中无需修改角色 Shader 的**单角色选择性后处理管线**——对特定 Layer 上的角色独立施加 Bloom + 色彩调整 + ACES 色调映射，场景其余部分不受影响。

## 特性

- **选择性后处理**：只对角色施加效果，场景背景完全不受影响
- **零 Shader 入侵**：无需修改角色 Shader
- **完整效果链**：Dual Kawase Bloom → OKLab 色彩调整 → 阈值制 ACES 色调映射
- **独立开关**：Bloom / 色彩调整 / 色调映射均可单独启用/禁用
- **BlendShape 兼容**：通过第二相机方案正确支持面部变形、GPU 蒙皮
- **Scene 视图安全**：仅 Game 相机触发，不影响编辑器

## 环境

| 组件 | 版本 |
|------|------|
| Unity | 2022.3.62f3c1 |
| URP | 14.0.12 (Core RP Library) |
| API | DirectX 11 / Windows |
| Shader Target | 3.0 |

## 文件结构

```
Assets/Genshen/Default/后处理/
├── CharacterPostProcessFeature.cs    # ScriptableRendererFeature
├── CharacterPostProcessPass.cs       # ScriptableRenderPass
├── MaskedCopy.shader           # Hidden/Character/MaskedCopy
├── CoverageExtract.shader      # Hidden/Character/CoverageExtract
├── CoverageDilate.shader       # Hidden/Character/CoverageDilate
├── Bloom.shader                # Unlit/Bloom (4-pass: Prefilter/KawaseDown/Up/Composite)
├── ColorAdjustments.shader     # Unlit/ColorAdjustments (OKLab Saturation + Vibrance + Hue)
├── SceneComposite.shader       # Hidden/Character/SceneComposite
└── ACESToneMapping.shader      # Unlit/ACESToneMapping (阈值制，保护中低调)
```

## 架构

```
Pass.Execute():
  1. 第二相机 RT → CoverageSrc → CoverageExtract → CoverageRT (R8) + Dilate
  2. Scene copy (camera → SceneRT)
  3. MaskedCopy (SceneRT × CoverageRT → CharRT)
  4. Bloom (CharRT → Kawase Dual → TempRT, 全白覆盖)
  5. ColorAdj (TempRT → AdjustedRT, OKLab 空间)
  6. Composite (AdjustedRT + SceneRT + Coverage → camera)
  7. ACES (仅 HDR 亮区压缩)
```

## 安装

1. 将 9 个文件放入 Unity 项目 `Assets/` 中任意目录
2. 在 **URP Renderer 资产** (Forward Renderer Data) 中点击 **Add Renderer Feature** → 选择 **CharacterPostProcessFeature**
3. 创建 5 个材质，分别使用以下 Shader，并拖入 Feature Inspector 对应槽位：

| 材质槽 | Shader |
|--------|--------|
| Bloom Material | `Unlit/Bloom` |
| Color Adjustments Material | `Unlit/ColorAdjustments` |
| Scene Composite Material | `Hidden/Character/SceneComposite` |
| Aces Tone Mapping Material | `Unlit/ACESToneMapping` |
| Mask Copy Material | `Hidden/Character/MaskedCopy` |

4. 设置 **Character Layer** 为你角色所在的 Layer
5. Play Mode — 所有滑块立即生效

## 参数说明

| 分类 | 参数 | 默认值 | 说明 |
|------|------|--------|------|
| Bloom | Enable Bloom | ✓ | 启用/禁用 Bloom |
| | Threshold | 0.9 | 高亮提取阈值 |
| | Soft Knee | 0.6 | 阈值过渡软度 |
| | Intensity | 1.5 | 柔光强度 |
| | Kawase Iterations | 3 | 模糊迭代次数 |
| Color | Enable Color Adjustments | ✓ | 启用/禁用色彩调整 |
| | Saturation | 1.0 | OKLab 空间饱和度 (0=灰度, 2=极度鲜艳) |
| | Vibrance | 0.0 | 自然饱和度 (仅增强低饱和区域) |
| | Hue Shift | 0.0 | 色相偏移 (-0.5~0.5) |
| | Color Filter | White | 颜色滤镜 |
| Tone | Enable Tone Mapping | ✓ | 启用/禁用色调映射 |
| | ToneMap Start Lum | 0.8 | 低于此亮度完全不经 ACES |
| | ToneMap Full Lum | 1.5 | 高于此亮度完整 ACES |
| | Exposure | 1.0 | 曝光度 |

## 技术文档

详细的技术教程（含设计决策、失败案例回溯、GPU 精度 Bug 定位全程）见 [TUTORIAL.md](TUTORIAL.md)。

## 许可证

MIT License
