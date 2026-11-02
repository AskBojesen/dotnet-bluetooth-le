using Plugin.BLE.Extensions;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace BLE.Client.WinConsole
{
    /// <summary>
    /// Demonstrations for communicating with an Egoo instrument using the Windows BLE API directly
    /// </summary>
    public class EgooDemos
    {
        private static readonly Guid CommonService = new("000000DD-0000-1000-8000-00805F9B34FB");
        private static readonly Guid InfoCharacteristic = new("0000DD01-0000-1000-8000-00805F9B34FB");
        private static readonly Guid AssayService = new("000000EE-0000-1000-8000-00805F9B34FB");
        private static readonly Guid DrawerCharacteristic = new("0000EE01-0000-1000-8000-00805F9B34FB");
        private static readonly Guid CapsuleCharacteristic = new("0000EE02-0000-1000-8000-00805F9B34FB");

        private readonly Action<string, object[]>? writer;
        private readonly ManualResetEvent disconnectedSignal = new ManualResetEvent(false);
        private readonly ManualResetEvent connectedSignal = new ManualResetEvent(false);

        public EgooDemos(Action<string, object[]>? writer = null)
        {
            this.writer = writer;
        }

        private void Write(string format, params object[] args)
        {
            writer?.Invoke(format, args);
        }

        public async Task Egoo_DEMO_Information()
        {
            string bleaddress = BleAddressSelector.GetBleAddress();
            ulong bleaddressUl = bleaddress.ToBleDeviceGuid().ToBleAddress();
            BluetoothLEDevice dev = await BluetoothLEDevice.FromBluetoothAddressAsync(bleaddressUl);
            dev.RequestPreferredConnectionParameters(BluetoothLEPreferredConnectionParameters.ThroughputOptimized);
            dev.ConnectionStatusChanged += Dev_ConnectionStatusChanged;
            var devId = BluetoothDeviceId.FromId(dev.DeviceId);
            Write("Connecting...");
            var stopwatch = Stopwatch.StartNew();
            GattSession gattSession = await GattSession.FromDeviceIdAsync(devId);
            gattSession.MaintainConnection = true;
            if (!connectedSignal.WaitOne(10000))
            {
                Write("Not Connected!!!");
                return;
            }
            Write("Connected in {0} ms", stopwatch.ElapsedMilliseconds);

            Write("Instrument info: {0}", await ReadJson(dev, CommonService, InfoCharacteristic));
            Write("Drawer state: {0}", await ReadJson(dev, AssayService, DrawerCharacteristic));
            Write("Capsule state: {0}", await ReadJson(dev, AssayService, CapsuleCharacteristic));

            stopwatch = Stopwatch.StartNew();
            gattSession.MaintainConnection = false;
            gattSession.Dispose();
            dev.Dispose();
            if (!disconnectedSignal.WaitOne(10000))
            {
                Write("Not Disconnected!!!");
                return;
            }
            Write("Disconnected in {0} ms", stopwatch.ElapsedMilliseconds);
        }

        private static async Task<string> ReadJson(BluetoothLEDevice dev, Guid serviceId, Guid characteristicId)
        {
            Exception? last = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    return await ReadJsonOnce(dev, serviceId, characteristicId);
                }
                catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
                {
                    last = ex;
                    await Task.Delay(1000);
                }
            }
            return $"Read cancelled after retries ({last?.Message})";
        }

        private static async Task<string> ReadJsonOnce(BluetoothLEDevice dev, Guid serviceId, Guid characteristicId)
        {
            var services = await dev.GetGattServicesForUuidAsync(serviceId, BluetoothCacheMode.Cached);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
                return $"Service not found ({services.Status})";
            var characteristics = await services.Services[0].GetCharacteristicsForUuidAsync(characteristicId, BluetoothCacheMode.Cached);
            if (characteristics.Status != GattCommunicationStatus.Success || characteristics.Characteristics.Count == 0)
                return $"Characteristic not found ({characteristics.Status})";
            var read = await characteristics.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached);
            if (read.Status != GattCommunicationStatus.Success)
                return $"Read failed ({read.Status})";
            using var reader = Windows.Storage.Streams.DataReader.FromBuffer(read.Value);
            return reader.ReadString(read.Value.Length);
        }

        private void Dev_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            Write("ConnectionStatusChanged:{0}", sender.ConnectionStatus);
            switch (sender.ConnectionStatus)
            {
                case BluetoothConnectionStatus.Disconnected:
                    disconnectedSignal.Set();
                    break;
                case BluetoothConnectionStatus.Connected:
                    connectedSignal.Set();
                    break;
            }
        }
    }
}
