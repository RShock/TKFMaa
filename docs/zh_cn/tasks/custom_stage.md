# 消耗体力：自定义活动关卡

## 功能目标

在“消耗体力”的“体力消耗关卡”下拉框中，直接选择唯一内置“日常调教本”或自定义关卡，并在同一步添加、管理多份命名配置。游戏更新后可重新截取，无需维护内置列表。

## 前置条件

- 先为当前实例连接ADB设备并加载资源扩展；自定义下拉选择和任务内管理入口在扩展加载后显示。
- 9:16竖屏，例如720×1280或1080×1920。先选择“活动页签”（默认限时活动，也可选每日活动）；活动入口须位于所选页签，进入后直接显示纵向关卡列表。
- 游戏已配置战斗队伍；点击关卡后应显示既有军团扫荡详情或底部开始按钮。

内置列表已移除过期活动和战争演习预设，仅保留日常调教本；用户新增的配置可随活动变化继续添加、删除。

## 选择、添加和管理

1. 打开“消耗体力”步骤的选项，“体力消耗关卡”同时显示内置关卡和本实例已保存的自定义关卡，自定义项以名称和“（自定义）”标注。
2. 下拉框末尾选择“＋ 添加自定义关卡…”，填写名称和单次体力消耗（1-999）。同一选项下的“管理自定义关卡”可编辑、重命名、复制或删除现有配置。
3. 点击“设置活动入口截图”，在游戏窗口切到活动封面列表。截图窗口直接复用助手右侧实时画面的缓存帧，点击“截取”冻结，再拖框选活动标题或稳定图案并确认。
4. 点击“设置关卡截图”，在游戏中进入活动并找到目标关卡，按相同步骤从右侧实时画面框选名称或稳定图案。另自动保存顶部作为关卡列表页面标记。
5. 在消耗体力任务中选择“活动页签”：默认“限时活动”，另一项为“每日活动”。该选择决定无状态路由先点击哪个页签。
6. 点击“保存并使用此关卡”，保存配置并在当前消耗体力步骤中选中该关卡。可关闭窗口继续设置扫荡补刷、体力药和体力购买开关。

切换配置保留编辑，关闭窗口放弃未保存修改；删除需二次点击并保存。任务按稳定配置ID关联，重命名后仍引用同一关卡；删除引用中的配置后显示缺失，执行前失败，要求重新选择。不同消耗体力步骤可以选择不同配置，保存不改变其他步骤的关联。

截图窗口只读取右侧实时画面缓存，不另启动 ADB 截图进程；页面操作仍在游戏窗口进行。图片按720×1280归一，拖框至少16×16且位于列表区域，避开菜单、底部导航、动画与倒计时；关卡顶部确认区域[50,60,600,80]不能被弹窗遮挡。

## 页面流程

```mermaid
flowchart TD
    Select[消耗体力：选择关卡] --> Kind{内置或自定义?}
    Kind -->|内置| Builtin[沿用原有路线]
    Kind -->|自定义| Config[按稳定ID读取图片和体力消耗]
    Config --> Valid{配置有效?}
    Valid -->|否| Invalid[动作前明确失败，不重启游戏]
    Valid -->|是| Router[无状态路由：同时检查出征、活动页签、活动入口图、关卡图]
    Router -->|命中出征| Home[点击出征]
    Home --> Router
    Router -->|命中活动页签图| Tab[点击限时活动/每日活动页签]
    Tab --> Router
    Router -->|命中活动入口图| Entry[点击活动入口]
    Entry --> Router
    Router -->|命中关卡图且页面标记一致| Stage[点击，再识别关卡详情]
    Router -->|仅列表页面标记| List{向下已滚动6次?}
    List -->|否| Swipe[向上滑动查看下方关卡]
    Swipe --> Router
    List -->|是| Failed[明确失败]
    Router -->|均未命中/超时| Failed
    Stage -->|军团扫荡| Sweep[复用扫荡与可选补刷]
    Stage -->|开始按钮| Battle[复用直接战斗和再次挑战]
    Stage -->|未确认并超时| Failed
    Sweep --> Main[确认返回主页后完成]
    Battle --> Main
    Failed --> Recovery{遇到故障时行为}
    Recovery -->|重启| Restart[唯一重启登录，再从主页进入]
    Restart --> Home
    Recovery -->|暂停| Pause[等待用户继续或停止]
    Pause --> Final[按失败继续策略停止或跳过]
```

## 关键分支和选项

- 前半段不依赖固定起点：低频循环检查主页出征按钮、所选活动页签、活动入口图和关卡图，命中哪个点哪个。活动页签下拉框默认限时活动，可切换每日活动；两项都复用TemplateMatch页签模板，不使用OCR。路由rate_limit为1000ms、最多30次命中/30秒，禁止无间隔快速扫描。
- 点击关卡和向下滚动都先确认列表顶部。每次先找目标，向下最多6次，第六次后仍检查画面，耗尽明确失败。
- 图片匹配复用MaaFramework TemplateMatch，入口/关卡阈值0.85，出征与两个活动页签阈值0.82，均不代表准确率。页签位置或选中态样式变化时重新录制Common页签模板。
- 自定义Case才会进入本动作；任务级profile_id为空时回退实例最后选择的配置，避免已保存配置因前端覆盖丢失而直接报未选择。
- 体力消耗用于OCR读取当前体力后判断是否足够，并非游戏输入项。继续沿用现有扫荡补刷、用药与购买开关；恢复资源默认关闭，额度不增加。
- 右侧实时画面未连接、缓存帧为空、横屏或图像无效时停止刷新并提示，用户恢复后主动刷新；截图窗口不另连接或切换设备。运行和连接时禁止编辑。
- 配置不完整、重名、非法体力消耗、图片缺失、路径越界和版本不兼容在动作前失败。导航和战斗异常沿用唯一重启恢复。

## 实现映射

| 模块 | 文件或节点 |
| --- | --- |
| 唯一用户任务 | assets/interface.json：消耗体力 / DailyStaminaStart |
| 内置列表和固定自定义存储项生成 | tools/build_stamina_activities.py / __TKFM_CUSTOM_STAGE__ |
| 下拉框扩展、当前任务内添加管理、右侧实时画面缓存裁剪及配置装配 | assets/resource/custom/TkfmCustomStage.cs |
| 任务稳定引用 | 体力消耗关卡Option.Data.custom_stage_profile_id；当前会话合并至pipeline_override.CustomStageConfig.attach.profile_id |
| 活动页签 | assets/interface.json：活动页签；Common/LimitedActivityTab720.png、Common/CurrentActivityTab720.png |
| 无状态图片导航 | custom_stage.json：CustomStageActivityRouter、CustomStageMainExpedition、CustomStageOpenLimitedActivities、CustomStageOpenCurrentActivities、CustomStageOpenEntry、CustomStageOpenTarget、CustomStageScrollDown |
| 详情确认与战斗 | CustomStageConfirmDetail、CustomStageDirectBattle、既有DailyStamina节点 |
| 唯一异常恢复 | CustomStageWithRecovery → DailyStaminaStartAttempt |
| 兼容保留的配置和图片 | 实例TkfmCustomStageProfilesV1、资源image/UserStages/ |

## 限制和完成状态

两图仅覆盖活动入口后直达纵向关卡列表的路线；不处理额外分类、横向分页、二维地图或自动编队。顶部标记变化后须重新截关卡图。重命名不影响ID关联，复制得到独立ID，删除不物理清理图片，以免破坏共享引用。备份需同时保留实例配置和图片目录；用户图片由Git忽略。

旧独立“自定义关卡”任务在扩展空闲加载时转换为“消耗体力”并保留配置引用和资源开关；日志与游戏设置不再提供自定义配置按钮。完成仍以既有流程确认主页为准，未知页面及次数耗尽不记为成功。
