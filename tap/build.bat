@echo off
rem tap.dll 编译脚本 —— zig cc 交叉出 x64 DLL（绿色工具链，无需 VS）
rem 用法：build.bat  （在 _tap 目录下执行；产物 tap.dll）
setlocal
set ZIG=zig
where zig >nul 2>&1 || set ZIG=D:\zig\zig.exe
set SRC=tap.c minhook\src\buffer.c minhook\src\hook.c minhook\src\trampoline.c minhook\src\hde\hde64.c
set INC=-Iminhook\include -Iminhook\src
%ZIG% cc -target x86_64-windows-gnu -shared -O2 -Wall -o tap.dll %SRC% %INC% -lkernel32 -lntdll
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo BUILD OK - tap.dll
dir /b tap.dll
