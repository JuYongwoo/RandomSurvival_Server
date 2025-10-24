// Clients.cs
using System.Net.Sockets;

public class Clients
{
    
    public TcpClient socket;
    public int playerId;
    public Vector2 position;

    public Clients(TcpClient client, int id)
    {

        //변하지 않는 값, 초기화 때 할당
        socket = client;
        playerId = id;
    }

    public void setPosition(Vector2 position){
        this.position = position;
    }

    public Vector2 getPosition(){
        return position;
    }
}
