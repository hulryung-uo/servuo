#region References
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;

using Server.Network;
#endregion

namespace Server.Misc
{
	public class ServerList
    {
        public static string ServerName { get; } = Config.Get("Server.Name", "My Shard");

        // Server.Address may be an IP literal or a hostname (e.g. a DDNS name). Hostnames are resolved
        // at startup and re-resolved every ResolveInterval so a changing public IP keeps working.
        private static readonly string AddressSetting = Config.Get("Server.Address", "127.0.0.1");
        private static readonly TimeSpan ResolveInterval = TimeSpan.FromMinutes(10.0);

        private static IPAddress m_Address;
        private static DateTime m_NextResolve;

        public static IPAddress Address { get { return ResolveAddress(); } }

        private static IPAddress ResolveAddress()
        {
            IPAddress literal;

            if (String.IsNullOrWhiteSpace(AddressSetting))
                return IPAddress.Loopback;

            if (IPAddress.TryParse(AddressSetting, out literal))
                return literal;

            if (m_Address != null && DateTime.UtcNow < m_NextResolve)
                return m_Address;

            m_NextResolve = DateTime.UtcNow + ResolveInterval;

            try
            {
                foreach (IPAddress ip in Dns.GetHostAddresses(AddressSetting))
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        if (!ip.Equals(m_Address))
                            Console.WriteLine("ServerList: '{0}' resolved to {1}", AddressSetting, ip);

                        m_Address = ip;
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("ServerList: could not resolve '{0}': {1}", AddressSetting, e.Message);
            }

            return m_Address ?? IPAddress.Loopback;
        }

        public static void Initialize()
        {
            Console.Title = ServerName;

            ResolveAddress();

			EventSink.ServerList += EventSink_ServerList;
		}

		private static void EventSink_ServerList(ServerListEventArgs e)
		{
			try
            {
                var ns = e.State;
                var s = ns.Socket;

                var ipep = (IPEndPoint)s.LocalEndPoint;

                var address = ipep.Address;
                var addressClient = ((IPEndPoint)s.RemoteEndPoint).Address;

                if (!IPAddress.IsLoopback(addressClient) && !IsPrivateNetwork(addressClient))
                {
                    address = Address;
                }

                e.AddServer(ServerName, new IPEndPoint(address, ipep.Port));
            }
			catch
			{
				e.Rejected = true;
			}
		}

		private static bool IsPrivateNetwork(IPAddress ip)
		{
			// 10.0.0.0/8
			// 172.16.0.0/12
			// 192.168.0.0/16
			// 169.254.0.0/16
			// 100.64.0.0/10 RFC 6598

			if (ip.AddressFamily == AddressFamily.InterNetworkV6)
			{
				return false;
			}

			if (Utility.IPMatch("192.168.*", ip))
			{
				return true;
			}

			if (Utility.IPMatch("10.*", ip))
			{
				return true;
			}

			if (Utility.IPMatch("172.16-31.*", ip))
			{
				return true;
			}

			if (Utility.IPMatch("169.254.*", ip))
			{
				return true;
			}

			if (Utility.IPMatch("100.64-127.*", ip))
			{
				return true;
			}

			return false;
		}
	}
}
