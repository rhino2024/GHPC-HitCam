# 命中回放小窗

Gunner, HEAT, PC! 的 MelonLoader 模组。炮弹打中车之后，屏幕右上角开一个几秒的小窗，绕着被打的那辆车转一圈，让你看清这次到底造成了什么伤。

## 你会看到什么

远距离一发穿过去，主视角里往往只看见远处闪了一下，车接着烧，你并不知道打中了什么。这个模组把那一瞬间单独放给你看。

命中之后，右上角出现一个小窗，镜头绕着那辆被击中的车转，车体上的伤口、被打到的模块和乘员都看得见，用的就是游戏自己在战后报告里的那套内构美术。窗口下面一行字写这次用的弹种、距离和穿深，伤害那句话直接用游戏自己的战报文本，所以它跟着你的游戏语言走，不会中英混着出。

窗口放几秒自己关掉，不用你操作。期间又来一次命中的话，新的一次直接接管窗口，不会排在旧的后面。

它只做大口径弹药打到载具这一件事。机枪子弹、破片、打空、打在地上都不开窗，这是有意的：那些命中本来就没什么可看，一直弹窗只会盖住战场。任务里随时按 F12 整个关掉或打开。

不碰游戏任何其他东西：不进战后报告，不改物理，不动别的车，游戏自己的报告一开始就让位。

## 装

1. 先彻底退出游戏。游戏开着的时候 dll 是锁住的，覆盖不了。
2. 把 zip 解压到游戏根目录，就是放着 `GHPC.exe` 的那一层。解压出来应该看到 `Bin\Mods\HitCam.dll`。
3. 开一次游戏。MelonLoader 会在 `Bin\UserData\MelonPreferences.cfg` 里写出 `[HitCam]` 这一段。

需要 MelonLoader v0.7.2（net35 运行时），下载包里没有它。它不改游戏任何文件，删掉那个 dll 就算卸载。

## 能改的东西

| 名字 | 默认 | 作用 |
| --- | --- | --- |
| Enabled | true | 总开关 |
| Duration | 3 | 窗口开多少秒 |
| WindowWidth / WindowHeight | 640 / 360 | 窗口尺寸，觉得挡视野就调小 |
| Margin | 24 | 离右上角多远，单位像素 |
| MinCaliberMm | 40 | 口径小于这个的弹药不开窗 |
| OrbitSpeed | 26 | 镜头环绕速度，度/秒 |
| Elevation | 10 | 镜头高出目标多少度 |
| DistanceScale | 0.72 | 镜头离目标多远 |
| FieldOfView | 24 | 镜头视野 |
| ShowInterior | true | 显示内构 |
| ShowInfoText | true | 显示弹种 / 距离 / 穿深那一行 |
| ShowFlightPath | false | 显示从炮口到目标的那条长线，默认关 |
| ToggleKey | F12 | 任务中的临时开关 |
| DebugLog | false | 出问题再开，会写 `Bin/Mods/HitCam.debug.log` |

## 先说清楚

小窗是命中之后现场绕着车拍的一段实时画面，不是录下来的视频，所以帧率比主视图低，看着比游戏本体顿一点。它的画面每秒最多重画 30 次，这是为了让窗口本身不太吃帧。

被击毁的车如果已经被别的模组撤掉，或者那辆车的内构美术本来就没加载，窗口里能看到的东西就少。

内构那一层在某些角度会被车体挡住一部分，绕着转的时候最明显。

## 想自己编译

不用 .NET SDK，也不用 Unity 编辑器，一个 dll 而已，用任何 Roslyn `csc` 对着游戏自带的托管程序集编就行：

```bash
GHPC_DIR="/path/to/Gunner HEAT PC/Bin" bash src/build.sh deploy
```

`deploy` 会把编出来的 dll 覆盖到 `<游戏目录>\Bin\Mods\`，所以先关游戏。不加 `deploy` 就只编到 `src/bin/`。

## In English

After your shell strikes a vehicle, a small window opens in the top-right corner and orbits the struck hull for a few seconds, showing the damage you just did with the game's own interior art, with the ammo, distance and penetration on a caption line. A newer hit replaces the open window instead of queueing behind it. Only large-caliber hits on vehicles open a window; machine gun fire, spall and misses deliberately do not. `F12` toggles it mid-mission, `Duration` / `WindowWidth` / `WindowHeight` / `MinCaliberMm` in `[HitCam]` set the rest.

MIT，与 Radian Simulations 无关。在当前 Steam 版上实测过，游戏更新有可能让它失效。
