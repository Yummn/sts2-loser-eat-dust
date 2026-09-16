# 败者食尘 / Loser Eat Dust

> 项目分类：个人项目 / 《杀戮尖塔 2》Mod / 节点回溯工具 / 持续维护

一个基于地图节点快照的回溯 Mod。它会保存本阶段已到达节点的初始状态，让玩家可以回到之前经过的节点重新开始；在角色正常防死效果结算后仍将死亡时，也可以拦截死亡并选择从当前节点重来。

当前版本：`v0.3.1`

## 下载与安装

- [v0.3.1（推荐）](https://github.com/Yummn/sts2-loser-eat-dust/releases/tag/v0.3.1)
- [全部 Releases](https://github.com/Yummn/sts2-loser-eat-dust/releases)

下载与游戏平台和版本对应的 ZIP，解压后将 `LoserEatDust` 文件夹完整放入游戏 `mods/` 目录。

目录至少包含：

```text
mods/LoserEatDust/
├─ LoserEatDust.dll
└─ LoserEatDust.json
```

## 兼容版本

- Android v0.103.2
- Android v0.110.1
- PC v0.107.1

不依赖 BaseLib，不含 PCK。

## 主要功能

- 进入地图节点时保存该节点的初始状态；
- 当前阶段内走过多少节点，就可以在这些节点之间选择并重新开始；
- 回到旧节点后，后续节点记录仍然保留；重新经过已有层时，会用新时间线的首次进入状态覆盖旧记录；
- 节点记录会同步保存尖塔银行余额，回溯时一并恢复；
- 支持死亡拦截：正常防死遗物和药水结算后，如果仍会死亡，可选择从本节点重来或接受死亡；
- 使用游戏公开的战斗 Hook，不修改死亡函数，也不删除原版存档函数。

## 使用

暂停菜单中会新增两个入口：

1. **节点 X/Y：第 N 层 · 类型**：切换要恢复的历史节点；
2. **败者食尘：从这里重来**：加载所选节点第一次进入时的状态。

本 Mod 不再提供普通“重试本房”功能，因此可以和已有 Quick Restart / 快速 SL Mod 同时安装。

## 参考

死亡拦截的交互思路参考了 Douvahkiin 的开源项目：

- [StS2-DeathIntercept](https://github.com/Douvahkiin/StS2-DeathIntercept)

败者食尘采用不同实现：通过 `ModHelper` 战斗 Hook 在死亡结算前阻止死亡，并复用自身节点快照完成恢复。

详细版本变化见各个 [Release](https://github.com/Yummn/sts2-loser-eat-dust/releases)。
