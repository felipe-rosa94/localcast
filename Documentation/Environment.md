# Ambiente de desenvolvimento

## Windows

- Windows 10/11 x64
- .NET 8 SDK
- Visual Studio 2022 com cargas "Desenvolvimento para desktop com .NET" e "Desenvolvimento para desktop com C++"
- CMake 3.20 ou superior

```
cd Windows
dotnet build LocalCast.sln
dotnet test LocalCast.sln
dotnet run --project src/LocalCast.App
```

DLL nativa (stub da Fase 1):

```
cmake -S Windows/native -B Windows/native/build
cmake --build Windows/native/build --config Release
```

## Android TV

- Android Studio atual com SDK 35
- JDK 17
- TV TCL com Android TV e depuração ADB por rede habilitada

Abrir `AndroidTV` no Android Studio. O wrapper do Gradle ainda não foi gerado: o Android Studio o cria na primeira sincronização, ou rode `gradle wrapper` uma vez com Gradle instalado.

```
adb connect <ip-da-tv>:5555
./gradlew :app:installDebug
```
