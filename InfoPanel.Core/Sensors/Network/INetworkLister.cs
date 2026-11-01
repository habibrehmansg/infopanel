using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;

namespace InfoPanel.Sensors.Network;

/// <summary>
/// One network interface as the operating system lists it, read as <see cref="NetworkInterface"/> reads it: each
/// property is read when asked and may throw.
/// </summary>
public interface INetworkInterface
{
    /// <summary>The system's id: the adapter's GUID in braces on Windows, its name (<c>en0</c>) on macOS and Linux.</summary>
    string Id { get; }

    /// <summary>Its name: <c>Wi-Fi</c> on Windows, <c>en0</c> on macOS and Linux.</summary>
    string Name { get; }

    string Description { get; }

    NetworkInterfaceType InterfaceType { get; }

    OperationalStatus Status { get; }

    /// <summary>Its link speed in bits a second; -1 when the system has none.</summary>
    long Speed { get; }

    /// <summary>Its unicast addresses, in the system's order.</summary>
    IReadOnlyList<IPAddress> Addresses { get; }
}

/// <summary>Lists the network interfaces the operating system has now, in its order.</summary>
public interface INetworkLister
{
    IReadOnlyList<INetworkInterface> List();
}

/// <summary>The interfaces .NET lists (<see cref="NetworkInterface.GetAllNetworkInterfaces"/>), each read through its <see cref="NetworkInterface"/>.</summary>
public sealed class SystemNetworkLister : INetworkLister
{
    public IReadOnlyList<INetworkInterface> List() =>
        NetworkInterface.GetAllNetworkInterfaces().Select(n => (INetworkInterface)new SystemInterface(n)).ToArray();

    private sealed class SystemInterface(NetworkInterface ni) : INetworkInterface
    {
        public string Id => ni.Id;
        public string Name => ni.Name;
        public string Description => ni.Description;
        public NetworkInterfaceType InterfaceType => ni.NetworkInterfaceType;
        public OperationalStatus Status => ni.OperationalStatus;
        public long Speed => ni.Speed;
        public IReadOnlyList<IPAddress> Addresses => ni.GetIPProperties().UnicastAddresses.Select(a => a.Address).ToArray();
    }
}
