# Sherlock

<div align="center">
    <img src="https://count.getloli.com/get/@mircodz-sherlock-tools?theme=asoul&padding=3" /><br>
</div>

# Introduction

A terminal-first cross-platform memory profiler and analyzer for .NET.

<img width="1568" height="789" alt="screenshot" src="https://github.com/user-attachments/assets/83631fd1-1020-4e0d-86d9-e58a284cc893" />

```
sl run --correlate -- dotnet run --project myapp    # launch + capture allocations
> snapshot                                          # coherent heap dump + provenance
> dominators                                        # biggest memory holders
> whoalloc 0x137e15ac0                              # who allocated this object?
```