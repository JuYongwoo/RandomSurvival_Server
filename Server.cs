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

            Console.WriteLine($"[클라이언트 접속] {client.Client.RemoteEndPoint} → playerID={assignedId}");

            // ID 전송
            SendMessage(newClient, $"ID:{assignedId}");

            Thread t = new Thread(HandleClient);         // 클라이언트 처리 스레드 생성
            t.Start(newClient);                          // 스레드 시작
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
                Console.WriteLine($"[수신] playerID={clientObj.playerId}, msg={msg}");

                // 저장 메시지 처리: "SAVE:" 로 시작하면 DB에 저장 시도
                if (msg.StartsWith("SAVE:", StringComparison.OrdinalIgnoreCase))
                {
                    string payload = msg.Substring(5).Trim(); // 접두사 제거
                    var saveData = ParseSavePayload(payload); // 파싱 시도

                    if (saveData != null)
                    {
                        // DB에 저장 (업서트)
                        bool ok = db.SaveOrUpdatePlayer(clientObj.playerId,
                                                        saveData.LvValue,
                                                        saveData.EXPValue,
                                                        saveData.WeaponName,
                                                        saveData.WeaponUpgreadeValue);
                        if (ok) SendMessage(clientObj, "SAVE:OK");
                        else    SendMessage(clientObj, "SAVE:FAIL");
                    }
                    else
                    {
                        SendMessage(clientObj, "SAVE:PARSE_ERROR");
                    }

                    // 저장 메시지는 브로드캐스트하지 않음(원하면 변경)
                    continue;
                }

                // 일반 메시지는 브로드캐스트 (기존 동작)
                Broadcast(clientObj, msg); //송신한 클라이언트를 제외한 모든 클라이언트에게 보낸다
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
            Console.WriteLine($"[클라이언트 종료] playerID={clientObj.playerId}");
        }
    }

    private void Broadcast(Clients sender, string message, int excludeId = -1)
    {
        byte[] data = Encoding.UTF8.GetBytes(message);
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
        byte[] data = Encoding.UTF8.GetBytes(message);
        try { clientObj.socket.GetStream().Write(data, 0, data.Length); }
        catch { }
    }

    // 저장 데이터 표현형
    private class SavePayload
    {
        public int LvValue;
        public int EXPValue;
        public string WeaponName;
        public int WeaponUpgreadeValue;
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
                if (root.TryGetProperty("LvValue", out var lv)) p.LvValue = lv.GetInt32();
                if (root.TryGetProperty("EXPValue", out var exp)) p.EXPValue = exp.GetInt32();
                if (root.TryGetProperty("WeaponName", out var wn)) p.WeaponName = wn.GetString();
                if (root.TryGetProperty("WeaponUpgreadeValue", out var wu)) p.WeaponUpgreadeValue = wu.GetInt32();
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
                if (key.Equals("LvValue", StringComparison.OrdinalIgnoreCase)) int.TryParse(val, out p.LvValue);
                else if (key.Equals("EXPValue", StringComparison.OrdinalIgnoreCase)) int.TryParse(val, out p.EXPValue);
                else if (key.Equals("WeaponName", StringComparison.OrdinalIgnoreCase)) p.WeaponName = val;
                else if (key.Equals("WeaponUpgreadeValue", StringComparison.OrdinalIgnoreCase)) int.TryParse(val, out p.WeaponUpgreadeValue);
            }
            // 최소한 WeaponName 혹은 LvValue 등 하나라도 채워졌으면 반환
            if (p.WeaponName != null || p.LvValue != 0 || p.EXPValue != 0 || p.WeaponUpgreadeValue != 0) return p;
        }
        catch { }

        return null; // 파싱 실패
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
            Console.WriteLine($"[DB 저장] playerID={playerId}, Lv={lv}, EXP={exp}, Weapon={weaponName}, WUp={weaponUpgrade}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DB 오류] {ex.Message}");
            return false;
        }
    }
}
