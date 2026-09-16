@echo off
rem Build tap.dll. Prefer zig cc if installed; fall back to MinGW x64 gcc
rem (this mingw distro ships no 64-bit libgcc_s.a, hence -static-libgcc).
rem Keep comments ASCII - cmd parses bat in ANSI codepage, CJK breaks it.
setlocal
set SRC=tap.c minhook\src\buffer.c minhook\src\hook.c minhook\src\trampoline.c minhook\src\hde\hde64.c
set INC=-Iminhook\include -Iminhook\src
if exist D:\zig\zig.exe (
  D:\zig\zig.exe cc -target x86_64-windows-gnu -shared -O2 -Wall -o tap.dll %SRC% %INC% -lkernel32 -lntdll
) else (
  D:\mingw64\bin\x86_64-w64-mingw32-gcc.exe -shared -O2 -Wall -Wno-builtin-declaration-mismatch -static-libgcc -o tap.dll %SRC% %INC% -lkernel32 -lntdll
)
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo BUILD OK - tap.dll
dir /b tap.dll
