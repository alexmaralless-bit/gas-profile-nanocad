# Среда разработки

## C#

Для C# не нужен `venv`. Нужны:

- .NET 6 SDK;
- DLL nanoCAD из установленного nanoCAD.

Пути на машине разработки (для примера):

```text
C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe
C:\Program Files\Nanosoft\nanoCAD x64 24.1
C:\Program Files\dotnet\sdk\6.0.428
```

Проект:

```text
GasProfile.sln
src/GasProfile.NanoCad/GasProfile.NanoCad.csproj
```

Сборка:

```powershell
dotnet restore .\GasProfile.sln
.\build.ps1
```

`build.ps1` задает `DOTNET_CLI_HOME`, `LOCALAPPDATA`, `APPDATA` и `NUGET_PACKAGES` внутри workspace, чтобы .NET CLI не писал служебные файлы в пользовательский профиль.

На текущем этапе `build.ps1` собирает напрямую проект nanoCAD-адаптера:

```text
src/GasProfile.NanoCad/GasProfile.NanoCad.csproj
```

Загрузка в nanoCAD:

```text
NETLOAD
bin\Debug\GasProfile.NanoCad.dll
GASPROF_ABOUT
```

Если сборка ругается на отсутствующие reference assemblies .NET Framework, нужно установить .NET Framework Developer Pack под целевую версию nanoCAD-плагинов.
nanoCAD 24.1 использует .NET 6 API DLL, поэтому проект таргетит:

```text
net6.0-windows
```
