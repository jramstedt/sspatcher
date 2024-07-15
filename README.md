# System Shock Resource Patcher
This is simple tool to merge multiple resource files into one.

Intended use is to merge resource files from [HackEd](https://github.com/inkyblackness/hacked) mods with system shock game files.  
See [Hacked modding support](https://github.com/inkyblackness/hacked/wiki/ModdingSupport)

```
SystemShockPatcher v1.0.0

Usage:
SystemShockPatcher <in> [<in>]... <out>
```

## Example
This will overlay CITBARK.RES with citbark.patch.res 
```
SystemShockPatcher C:/GAMES/SSHOCK/RES/DATA/CITBARK.RES citbark.patch.res res/citbark.res
```
