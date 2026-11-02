using Plugin.BLE.Extensions;
using System;
using System.Collections.Generic;
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

        private static readonly (Guid Service, Guid Characteristic, string Name)[] NotifyCharacteristics =
        {
            (new("000000CC-0000-1000-8000-00805F9B34FB"), new("0000CC01-0000-1000-8000-00805F9B34FB"), "OtaInitialize"),
            (new("000000CC-0000-1000-8000-00805F9B34FB"), new("0000CC02-0000-1000-8000-00805F9B34FB"), "OtaWrite"),
            (new("000000CC-0000-1000-8000-00805F9B34FB"), new("0000CC03-0000-1000-8000-00805F9B34FB"), "OtaActivate"),
            (AssayService, DrawerCharacteristic, "Drawer"),
            (AssayService, CapsuleCharacteristic, "Capsule"),
            (AssayService, new("0000EE03-0000-1000-8000-00805F9B34FB"), "CapsuleDecode"),
            (AssayService, new("0000EE04-0000-1000-8000-00805F9B34FB"), "StartAnalysis"),
            (AssayService, new("0000EE05-0000-1000-8000-00805F9B34FB"), "Progress"),
            (AssayService, new("0000EE06-0000-1000-8000-00805F9B34FB"), "Result"),
            (AssayService, new("0000EE07-0000-1000-8000-00805F9B34FB"), "Error"),
            (CommonService, new("0000DD02-0000-1000-8000-00805F9B34FB"), "State"),
            (AssayService, new("0000EE0B-0000-1000-8000-00805F9B34FB"), "CustomAssayStart"),
            (AssayService, new("0000EE0C-0000-1000-8000-00805F9B34FB"), "PdCalibration"),
            (AssayService, new("0000EE0D-0000-1000-8000-00805F9B34FB"), "OpticalSelfCheck"),
            (AssayService, new("0000EE0E-0000-1000-8000-00805F9B34FB"), "AssayStop"),
            (CommonService, new("0000DD03-0000-1000-8000-00805F9B34FB"), "CallService"),
            (AssayService, new("0000EE12-0000-1000-8000-00805F9B34FB"), "CalibrationUpdatesStore"),
        };

        /// <summary>
        /// Mimics EgooInstrument NotifyHandler.SubscribeToAllChanges and times every step
        /// </summary>
        public async Task Egoo_ALL_Services()
        {
            var overall = Stopwatch.StartNew();
            var connection = await Connect();
            if (connection is null)
                return;
            var (dev, gattSession) = connection.Value;
            long connectMs = overall.ElapsedMilliseconds;

            var total = Stopwatch.StartNew();
            int index = 0;
            var services = new Dictionary<Guid, GattDeviceService>();
            var subscribed = new List<GattCharacteristic>();
            foreach (var (serviceId, characteristicId, name) in NotifyCharacteristics)
            {
                var sw = Stopwatch.StartNew();
                Write("[{0}/{1}] Subscribing to {2}...", ++index, NotifyCharacteristics.Length, name);
                try
                {
                    if (!services.TryGetValue(serviceId, out var service))
                    {
                        var sr = await dev.GetGattServicesForUuidAsync(serviceId, BluetoothCacheMode.Cached);
                        if (sr.Status != GattCommunicationStatus.Success || sr.Services.Count == 0)
                        {
                            Write("{0}: service not found ({1})", name, sr.Status);
                            continue;
                        }
                        service = sr.Services[0];
                        services[serviceId] = service;
                    }
                    long tService = sw.ElapsedMilliseconds;
                    var cr = await service.GetCharacteristicsForUuidAsync(characteristicId, BluetoothCacheMode.Cached);
                    if (cr.Status != GattCommunicationStatus.Success || cr.Characteristics.Count == 0)
                    {
                        Write("{0}: characteristic not found ({1})", name, cr.Status);
                        continue;
                    }
                    var characteristic = cr.Characteristics[0];
                    long tChar = sw.ElapsedMilliseconds;
                    var cccd = characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate)
                        ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
                        : GattClientCharacteristicConfigurationDescriptorValue.Notify;
                    var wr = await characteristic.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(cccd);
                    characteristic.ValueChanged += Characteristic_ValueChanged;
                    subscribed.Add(characteristic);
                    Write("{0}: {1} (service {2} ms, characteristic {3} ms, subscribe {4} ms)",
                        name, wr.Status, tService, tChar - tService, sw.ElapsedMilliseconds - tChar);
                }
                catch (Exception ex)
                {
                    Write("{0}: exception after {1} ms: {2}", name, sw.ElapsedMilliseconds, ex.Message);
                }
            }
            Write("Subscribed to {0}/{1} characteristics in {2} ms", subscribed.Count, NotifyCharacteristics.Length, total.ElapsedMilliseconds);
            Write("TOTAL: connect {0} ms + subscribe {1} ms = {2} ms until ready", connectMs, total.ElapsedMilliseconds, overall.ElapsedMilliseconds);

            foreach (var characteristic in subscribed)
            {
                characteristic.ValueChanged -= Characteristic_ValueChanged;
            }
            foreach (var service in services.Values)
            {
                service.Dispose();
            }

            Write("Disconnecting...");
            var stopwatch = Stopwatch.StartNew();
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

        private async Task<(BluetoothLEDevice Device, GattSession Session)?> Connect()
        {
            connectedSignal.Reset();
            disconnectedSignal.Reset();
            var sw = Stopwatch.StartNew();
            string bleaddress = BleAddressSelector.GetBleAddress();
            ulong bleaddressUl = bleaddress.ToBleDeviceGuid().ToBleAddress();
            Write("Getting BluetoothLEDevice for {0}...", bleaddress);
            BluetoothLEDevice? dev = await BluetoothLEDevice.FromBluetoothAddressAsync(bleaddressUl);
            if (dev is null)
            {
                Write("Device {0} not found after {1} ms", bleaddress, sw.ElapsedMilliseconds);
                return null;
            }
            Write("Got device '{0}' in {1} ms (status: {2})", dev.Name, sw.ElapsedMilliseconds, dev.ConnectionStatus);
            dev.RequestPreferredConnectionParameters(BluetoothLEPreferredConnectionParameters.ThroughputOptimized);
            dev.ConnectionStatusChanged += Dev_ConnectionStatusChanged;
            var devId = BluetoothDeviceId.FromId(dev.DeviceId);

            Write("Creating GattSession...");
            GattSession gattSession = await GattSession.FromDeviceIdAsync(devId);
            gattSession.MaintainConnection = true;
            Write("GattSession created at {0} ms, waiting for connection...", sw.ElapsedMilliseconds);

            if (dev.ConnectionStatus == BluetoothConnectionStatus.Connected)
            {
                connectedSignal.Set();
            }
            const int timeoutMs = 10000;
            for (int waited = 0; !connectedSignal.WaitOne(1000); waited += 1000)
            {
                if (waited + 1000 >= timeoutMs)
                {
                    Write("Not Connected after {0} ms!!! (status: {1}, session: {2})",
                        sw.ElapsedMilliseconds, dev.ConnectionStatus, gattSession.SessionStatus);
                    gattSession.Dispose();
                    dev.Dispose();
                    return null;
                }
                Write("  still waiting... {0} ms (session: {1})", sw.ElapsedMilliseconds, gattSession.SessionStatus);
            }
            var conpar = dev.GetConnectionParameters();
            Write("Connected in {0} ms (Interval = {1}, Latency = {2}, Timeout = {3}, MaxPdu = {4})",
                sw.ElapsedMilliseconds, conpar.ConnectionInterval, conpar.ConnectionLatency, conpar.LinkTimeout, gattSession.MaxPduSize);
            return (dev, gattSession);
        }

        private void Characteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            using var reader = Windows.Storage.Streams.DataReader.FromBuffer(args.CharacteristicValue);
            Write("ValueChanged {0}: {1}", sender.Uuid, reader.ReadString(args.CharacteristicValue.Length));
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
