@echo off
for %%i in (FortniteClient-Win64) do (
    Reg.exe delete "HKLM\Software\Policies\Microsoft\Windows\QoS\%%i" /f
)
Reg.exe delete "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\FortniteClient-Win64-Shipping.exe\PerfOptions" /f
Reg.exe delete "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\FortniteClient-Win64-Shipping.exe" /v "UseLargePages" /f
