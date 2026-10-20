# 前端 Bug 报告：Mods 页排序下拉被 mod 行盖住

> **与本次重构无关。** 已核实：4 个提交（dff052c / f5212cf / 4610108 / 036fdbd）
> 没有碰过任何 `.razor` / `.css` / `.js` / `.xaml` 文件，前端一行未动。
> 这是仓库里已存在的问题。

## 现象

Mods 页点「排序方式」下拉框，菜单出现在 mod 卡片下方/被 mod 行盖住，点不到菜单项。

## 根因

`wwwroot/css/mods.css:372` 的 `.jg-sort-menu` 有 `z-index: 1000`，
但它所在的 `.jg-sort-dd`（:359）只有 `position: relative`，**没有建立层叠上下文提升**。

mod 行列表（`.jg-modrows`）在 DOM 中位于 toolbar **之后**，且行上有列表入场动画残留的
恒等 `transform` —— 这会自建层叠上下文。于是：

- `.jg-sort-menu` 的 `z-index:1000` 只在 `.jg-sort-dd` 内部生效
- 后续的 `.jg-modrow` 整体绘制在 toolbar 之上
- 排序菜单被行盖住

## 仓库里已有同构的修复（三次验证）

`mods.css:549`：
```css
/* v1.5.1：行尾菜单打开时提升所在行 —— 行上残留的恒等 transform（列表入场动画）
   会创建层叠上下文，菜单的 z-index 只在行内生效，会被后绘制的兄弟行盖住。
   用 :has() 把打开菜单的行提到兄弟之上。
   v1.5.3：z-index → 9500 —— 必须高于遮罩 .jg-dd-overlay(9000)，否则遮罩铺开后
   会盖住菜单导致菜单项点不到；同时压过顶部 sticky 渐变(.jg-al-gradient=50)，
   否则菜单上半截（含「更新到 x」）会被渐变压住。 */
.jg-modrow:has(.jg-modmenu.open) { position: relative; z-index: 9500; }
```

**这正是同一个病**：行尾「⋮」菜单当年也是被兄弟行盖住，v1.5.1 用 `:has()` 修掉。
排序下拉漏了同样的处理。

另外 `mods.css:95` 还有一处 `.jg-bundle-subs:has(.jg-modmenu.open) { overflow: visible; }`，
同属这一族。

## 建议修复

在 `mods.css` 的 `.jg-sort-dd.open .jg-sort-menu`（:389）附近加一条，
与 v1.5.3 的取值对齐：

```css
/* 排序下拉打开时把所在行提到 mod 行之上 —— 与 .jg-modrow:has(.jg-modmenu.open) 同因：
   toolbar 在 DOM 中先于 .jg-modrows，行上入场动画的恒等 transform 自建层叠上下文，
   光给 .jg-sort-menu z-index 只在本容器内生效，压不住后绘制的行（2026-10-20 实测）。
   9500 = 高于 .jg-dd-overlay(9000) 与 .jg-al-gradient(50)，与 v1.5.3 对齐。 */
.jg-mods-toolbar:has(.jg-sort-dd.open) { position: relative; z-index: 9500; }
```

### 为什么是这个选择器

| 方案 | 评价 |
|---|---|
| `.jg-mods-toolbar:has(.jg-sort-dd.open)` | **推荐**。与 v1.5.3 同构、同值；`:has()` 仓库已用 4 处，WebView2 内核支持 |
| `.jg-sort-dd.open { z-index: 9500 }` | 不够。`.jg-sort-dd` 只是 relative，提它自己仍在 toolbar 的层叠上下文里 |
| 去掉行上的 transform | 会改掉入场动画，波及面大 |
| 把 `.jg-sort-menu` 改成 `position: fixed` | 要 JS 重算坐标，改动最大 |

### 影响面

- 只作用于 `.jg-mods-toolbar`，且只在 `.jg-sort-dd.open` 时生效
- 不碰 `.jg-modrow` / `.jg-modmenu`（v1.5.3 的既有修复）
- 关闭时无任何样式变化，无回归面

## 顺带发现（本次未验证，仅记录）

审计报告 04 里标注过两个 CSS 重复声明问题，与这个 bug 无关但同源：

1. `.jg-modal-overlay` 在 `effects.css:213` 与 `misc.css:154` 各定义一次，值不同，靠加载顺序决胜
2. `.jg-shimmer-line` 的 `height:14px`（`effects.css:311`）被 `detail.css:52` 的 `12px` 覆盖，是死声明

这两处按约定未动。
