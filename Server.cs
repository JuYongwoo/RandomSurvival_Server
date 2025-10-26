// Server.cs
// 빌드 전: dotnet add package Microsoft.Data.Sqlite
using System;                                     // 기본 입출력 사용
using System.Collections.Generic;                 // List<T> 사용
using System.Net;                                 // IPAddress 사용
using System.Net.Sockets;                         // TcpListener/TcpClient 사용
using System.Text;                                // Encoding 사용
using System.Threading;                           // Thread 사용
using System.Text.Json;                           // JSON 파싱
using Microsoft.Data.Sqlite;                      // SQLite 사용 (NuGet 필요)

public struct Vector2{                             // Vector2 구조체 (원본 유지)
    public Vector2(float x, float y){ this.x = x; this.y = y; }
    float x;
    float y;
}

public class Server
{
    public List<Clients> clients = new List<Clients>(); // 접속 클라이언트 리스트
    public object locker = new object();               // 동기화 락
    private int nextPlayerId = 0;                      // 다음 할당할 playerId

    private DatabaseManager db;                        // DB 매니저 인스턴스

    public Server()
    {
        db = new DatabaseManager("players.db");        // DB 파일 경로 지정
    }

    public void Start()
    {
        int port = 5000;                               // 리스닝 포트
        TcpListener server = new TcpListener(IPAddress.Any, port); // 리스너 생성
        server.Start();                                // 리스너 시작
        Console.WriteLine($"[서버 실행 중] 포트 {port}");

        while (true)
        {
            TcpClient client = server.AcceptTcpClient(); // 클라이언트 연결 수락

            int assignedId;
            Clients newClient;
            lock (locker)
            {
                assignedId = nextPlayerId++;             // ID 할당
                newClient = new Clients(client, assignedId);
                clients.Add(newClient);                  // 리스트에 추가
            }

            Console.WriteLine($"{assignedId}:ENTER:{client.Client.RemoteEndPoint}");

            // ID 전송 (개행 포함)
            SendMessage(newClient, $"{assignedId}:ID:{assignedId}");
            Broadcast(newClient, $"{assignedId}:ENTER");

            Thread t = new Thread(HandleClient);         // 클라이언트 하나 당 처리할 스레드 할당, HandleClient(무한 루프)
            t.Start(newClient);
        }
    }

    private void HandleClient(object obj)
    {
        Clients clientObj = (Clients)obj;               // 객체 캐스팅
        TcpClient client = clientObj.socket;             // TcpClient 획득
        NetworkStream stream = client.GetStream();       // 네트워크 스트림 획득
        byte[] buffer = new byte[4096];                  // 수신 버퍼
        int bytesRead;

        try
        {
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                string msg = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim(); // 메시지 문자열로 변환
                Console.WriteLine($"{clientObj.playerId}:{msg}");

                if (msg.Contains("SAVE:", StringComparison.OrdinalIgnoreCase))
                {
                    string payload = msg.Substring(5).Trim(); // 접두사 제거
                    var saveData = ParseSavePayload(payload); // 파싱 시도

                    if (saveData != null)
                    {
                        bool ok = db.SaveOrUpdatePlayer(clientObj.playerId, saveData.Lv, saveData.Exp, saveData.WeaponName, saveData.WeaponUpgrade);
                        if (ok) SendMessage(clientObj, "SERVER:SAVE:OK");
                        else    SendMessage(clientObj, "SERVER:SAVE:FAIL");
                    }
                    else
                    {
                        SendMessage(clientObj, "SERVER:SAVE:PARSE_ERROR");
                    }

                    // 저장 메시지는 브로드캐스트하지 않음
                    continue;
                }

                if (msg.Contains("POSITION:", StringComparison.OrdinalIgnoreCase))
                {
                    string payload = msg.Substring(9).Trim();
                    var pos = ParsePositionPayload(payload);
                    if (pos != null)
                    {
                        // 브로드캐스트할 JSON 생성 (보내는 클라이언트의 서버측 playerId 포함)
                        var outgoing = new {
                            PlayerId = clientObj.playerId,
                            x = pos.Value.x,
                            y = pos.Value.y,
                            z = pos.Value.z
                        };
                        // string outJson = JsonSerializer.Serialize(outgoing);
                        Broadcast(clientObj, $"{clientObj.playerId}:POSITION:{outgoing.x},{outgoing.y},{outgoing.z}");
                        SendMessage(clientObj, "SERVER:POSITION:OK");
                    }
                    else
                    {
                        SendMessage(clientObj, "SERVER:POSITION:PARSE_ERROR");
                    }
                    continue;
                }

                Broadcast(clientObj, $"{clientObj.playerId}:{msg}"); //송신한 클라이언트를 제외한 모든 클라이언트에게 보낸다
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"클라이언트 오류: {ex.Message}");
        }
        finally
        {
            lock (locker) clients.Remove(clientObj);     // 연결 종료 시 리스트에서 제거
            client.Close();                              // 소켓 닫기
            Console.WriteLine($"{clientObj.playerId}:EXIT");
            Broadcast(clientObj, $"{clientObj.playerId}:EXIT");
        }
    }

    private void Broadcast(Clients sender, string message, int excludeId = -1)
    {
        // 메시지 끝에 개행을 붙여 클라이언트가 줄 단위로 읽을 수 있게 함
        byte[] data = Encoding.UTF8.GetBytes(message + "\n");
        lock (locker)
        {
            foreach (var c in clients)
            {
                if (c != sender)
                {
                    if (excludeId != -1 && c.playerId == excludeId) continue;

                    try { c.socket.GetStream().Write(data, 0, data.Length); }
                    catch { }
                }
            }
        }
    }

    private void SendMessage(Clients clientObj, string message)
    {
        // 메시지 끝에 개행 추가
        byte[] data = Encoding.UTF8.GetBytes(message + "\n");
        try { clientObj.socket.GetStream().Write(data, 0, data.Length); }
        catch { }
    }

    // 저장 데이터 표현형
    private class SavePayload
    {
        public int Lv;
        public int Exp;
        public string WeaponName;
        public int WeaponUpgrade;
    }

    // 페이로드 파싱: JSON 우선, 실패하면 key=value;key2=value2 또는 pipe 구분자 지원
    private SavePayload ParseSavePayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;

        // 1) JSON 시도
        try
        {
            using (JsonDocument doc = JsonDocument.Parse(payload))
            {
                var root = doc.RootElement;
                var p = new SavePayload();
                if (root.TryGetProperty("Lv", out var lv)) p.Lv = lv.GetInt32();
                if (root.TryGetProperty("Exp", out var exp)) p.Exp = exp.GetInt32();
                if (root.TryGetProperty("WeaponName", out var wn)) p.WeaponName = wn.GetString();
                if (root.TryGetProperty("WeaponUpgrade", out var wu)) p.WeaponUpgrade = wu.GetInt32();
                return p;
            }
        }
        catch { /* JSON 파싱 실패 -> 다음 시도 */ }

        // 2) key=value;key2=value2 또는 | 구분자 시도
        try
        {
            var p = new SavePayload();
            // 구분자로 세미콜론 또는 | 사용 허용
            string[] parts = payload.Split(new char[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var kv = part.Split(new char[] { '=' }, 2);
                if (kv.Length != 2) continue;
                var key = kv[0].Trim();
                var val = kv[1].Trim();
                if (key.Equals("Lv", StringComparison.OrdinalIgnoreCase)) int.TryParse(val, out p.Lv);
                else if (key.Equals("Exp", StringComparison.OrdinalIgnoreCase)) int.TryParse(val, out p.Exp);
                else if (key.Equals("WeaponName", StringComparison.OrdinalIgnoreCase)) p.WeaponName = val;
                else if (key.Equals("WeaponUpgrade", StringComparison.OrdinalIgnoreCase)) int.TryParse(val, out p.WeaponUpgrade);
            }
            // 최소한 하나의 값이 들어있으면 반환
            if (p.WeaponName != null || p.Lv != 0 || p.Exp != 0 || p.WeaponUpgrade != 0) return p;
        }
        catch { }

        return null; // 파싱 실패
    }

    // 위치 데이터 표현형 (내부 사용)
    private struct PositionPayload { public int x; public int y; public int z; }

    // 위치 페이로드 파싱: JSON, "x,y,z", 또는 key=value;key=value 형식 지원
    private PositionPayload? ParsePositionPayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;

        // 1) JSON 시도: {"x":1,"y":2,"z":3} 또는 array [1,2,3]
        try
        {
            using (JsonDocument doc = JsonDocument.Parse(payload))
            {
                var root = doc.RootElement;
                var p = new PositionPayload();
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 3)
                {
                    p.x = root[0].GetInt32();
                    p.y = root[1].GetInt32();
                    p.z = root[2].GetInt32();
                    return p;
                }
                if (root.TryGetProperty("x", out var px)) p.x = px.GetInt32();
                if (root.TryGetProperty("y", out var py)) p.y = py.GetInt32();
                if (root.TryGetProperty("z", out var pz)) p.z = pz.GetInt32();
                // 최소 하나 이상 세팅되었으면 반환 (완전성은 호출자에서 판단 가능)
                return p;
            }
        }
        catch { /* JSON 파싱 실패 -> 다음 시도 */ }

        // 2) CSV 형태 "1,2,3"
        try
        {
            var parts = payload.Split(new char[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                int x, y, z;
                if (int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y) && int.TryParse(parts[2], out z))
                {
                    return new PositionPayload() { x = x, y = y, z = z };
                }
            }
        }
        catch { }

        // 3) key=value;key2=value2 형식
        try
        {
            var parts = payload.Split(new char[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
            var p = new PositionPayload();
            foreach (var part in parts)
            {
                var kv = part.Split(new char[] { '=' }, 2);
                if (kv.Length != 2) continue;
                var key = kv[0].Trim();
                var val = kv[1].Trim();
                if (key.Equals("x", StringComparison.OrdinalIgnoreCase)) int.TryParse(val, out p.x);
                else if (key.Equals("y", StringComparison.OrdinalIgnoreCase)) int.TryParse(val, out p.y);
                else if (key.Equals("z", StringComparison.OrdinalIgnoreCase)) int.TryParse(val, out p.z);
            }
            // 최소 하나 이상 유효하면 반환
            return p;
        }
        catch { }

        return null;
    }
}

// DatabaseManager: 간단한 SQLite CRUD 처리
public class DatabaseManager
{
    private string _dbPath;                   // DB 파일 경로
    private string _connString;               // 연결 문자열

    public DatabaseManager(string dbPath)
    {
        _dbPath = dbPath;
        _connString = $"Data Source={_dbPath}"; // SQLite 연결 문자열
        EnsureTable();                          // 테이블 생성(없으면)
    }

    // 테이블이 없으면 생성
    private void EnsureTable()
    {
        using (var conn = new SqliteConnection(_connString))
        {
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Players (
                    PlayerId INTEGER PRIMARY KEY,
                    Lv INTEGER,
                    Exp INTEGER,
                    WeaponName TEXT,
                    WeaponUpgrade INTEGER,
                    LastUpdated TEXT
                );";
            cmd.ExecuteNonQuery();
        }
    }

    // 플레이어 저장 또는 업데이트 (업서트)
    public bool SaveOrUpdatePlayer(int playerId, int lv, int exp, string weaponName, int weaponUpgrade)
    {
        try
        {
            using (var conn = new SqliteConnection(_connString))
            {
                conn.Open();
                // 이미 있으면 업데이트, 없으면 INSERT
                var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO Players (PlayerId, Lv, Exp, WeaponName, WeaponUpgrade, LastUpdated)
                    VALUES ($id, $lv, $exp, $wname, $wup, $time)
                    ON CONFLICT(PlayerId) DO UPDATE SET
                        Lv = excluded.Lv,
                        Exp = excluded.Exp,
                        WeaponName = excluded.WeaponName,
                        WeaponUpgrade = excluded.WeaponUpgrade,
                        LastUpdated = excluded.LastUpdated;";
                cmd.Parameters.AddWithValue("$id", playerId);
                cmd.Parameters.AddWithValue("$lv", lv);
                cmd.Parameters.AddWithValue("$exp", exp);
                cmd.Parameters.AddWithValue("$wname", weaponName ?? "");
                cmd.Parameters.AddWithValue("$wup", weaponUpgrade);
                cmd.Parameters.AddWithValue("$time", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            Console.WriteLine($"[DB 저장] playerID={playerId}, Lv={lv}, Exp={exp}, Weapon={weaponName}, WUp={weaponUpgrade}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB 오류] {ex.Message}");
            return false;
        }
    }
}
