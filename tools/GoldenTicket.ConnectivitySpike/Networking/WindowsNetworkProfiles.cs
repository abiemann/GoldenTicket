using System.Collections;
using System.Runtime.InteropServices;

namespace GoldenTicket.ConnectivitySpike.Networking;

/// <summary>RFC1918 addresses also occur on public Wi-Fi. Consult Windows' actual network category.</summary>
internal static class WindowsNetworkProfiles
{
    internal static IReadOnlySet<Guid> ReadPrivateAdapters()
    {
        var categories = new Dictionary<Guid, bool>();
        object? manager = null;
        try
        {
            manager = Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"), throwOnError: true)!);
            dynamic networks = ((dynamic)manager!).GetNetworks(1); // connected networks
            try
            {
                foreach (object network in (IEnumerable)networks)
                {
                    try
                    {
                        var isPrivate = (int)((dynamic)network).GetCategory() == 1;
                        object connections = ((dynamic)network).GetNetworkConnections();
                        try
                        {
                            foreach (object connection in (IEnumerable)connections)
                            {
                                try
                                {
                                    var adapterId = ((INetworkConnection)connection).GetAdapterId();
                                    categories[adapterId] = isPrivate && categories.GetValueOrDefault(adapterId, true);
                                }
                                finally { Marshal.ReleaseComObject(connection); }
                            }
                        }
                        finally { Marshal.ReleaseComObject(connections); }
                    }
                    finally { Marshal.ReleaseComObject(network); }
                }
            }
            finally { Marshal.ReleaseComObject(networks); }
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or
            Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // Unknown category is not evidence that serving the LAN is safe.
            categories.Clear();
        }
        finally { if (manager is not null) Marshal.ReleaseComObject(manager); }
        return categories.Where(item => item.Value).Select(item => item.Key).ToHashSet();
    }

    // SDK netlistmgr.h, INetworkConnection. GUID-returning methods require typed COM marshalling;
    // late-bound IDispatch cannot marshal GUID records reliably on Windows.
    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        [return: MarshalAs(UnmanagedType.IDispatch)] object GetNetwork();
        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
    }
}
