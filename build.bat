@echo off
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\msbuild.exe APRSServer.csproj
del data\aprs.pdb
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\msbuild.exe APRSGateWayGui.csproj
del data\aprsgatewaygui.pdb
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\msbuild.exe APRSGateWay.csproj
del data\aprsgateway.pdb
rmdir /s /q obj
pause
