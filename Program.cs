using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Generic;

public class Program
{
    static private void Main(string[] args)
    {
        Server server = new Server();
        server.Start();
    }
}
