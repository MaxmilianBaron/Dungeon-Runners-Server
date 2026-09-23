# DRS-NET

Dungeon Runners server. Windows x64; [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

1. Download and extract **Code → Download ZIP**.
2. Extract `DungeonRunnersServer/Database/data.zip` into `Database`; run `Server.exe`.
3. Set the `[AuthServer]` section in the client's `config/DungeonRunners.cfg`:

   ```ini
   [AuthServer]
   Address = 127.0.0.1
   Port = 2110
   ```

4. From the client directory: `.\DungeonRunners.exe ran_from_launcher`.

Accounts are created on first local login. Rebuild: `Build.exe` with .NET 10 SDK.
