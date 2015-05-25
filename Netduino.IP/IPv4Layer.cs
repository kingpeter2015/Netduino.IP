using Microsoft.SPOT;
using System;
using System.Threading;

namespace Netduino.IP
{
    internal class IPv4Layer : IDisposable 
    {

        /*** TODO: we need to set our IPv4Address in the ArpResolver _every time_ that our IPAddress changes (and also when our IP address is first set, via DHCP or static IP) ***/
        /*** TODO: we should re-send our gratuitious ARP every time that our IP address changes (via DHCP or otherwise), and also every time our network link goes up ***/
        /*** TODO: if our IP address changes, should we update the source IP address on all of our sockets?  Should we close any active connection-based sockets?  ***/

        /* PUT THIS LOGIC IN OUR IPv4 class, and also hooked into from our DHCP class! */
        //void _ipv4_IPv4AddressChanged(object sender, uint ipAddress)
        //{
        //    Type networkChangeListenerType = Type.GetType("Microsoft.SPOT.Net.NetworkInformation.NetworkChange+NetworkChangeListener, Microsoft.SPOT.Net");
        //    if (networkChangeListenerType != null)
        //    {
        //        // create instance of NetworkChangeListener
        //        System.Reflection.ConstructorInfo networkChangeListenerConstructor = networkChangeListenerType.GetConstructor(new Type[] { });
        //        object networkChangeListener = networkChangeListenerConstructor.Invoke(new object[] { });

        //        // now call the ProcessEvent function to create a NetworkEvent class.
        //        System.Reflection.MethodInfo processEventMethodType = networkChangeListenerType.GetMethod("ProcessEvent");
        //        object networkEvent = processEventMethodType.Invoke(networkChangeListener, new object[] { (UInt32)(((UInt32)2 /* AddressChanged*/)), (UInt32)0, DateTime.Now }); /* TODO: should this be DateTime.Now or DateTime.UtcNow? */

        //        // and finally call the static NetworkChange.OnNetworkChangeCallback function to raise the event.
        //        Type networkChangeType = Type.GetType("Microsoft.SPOT.Net.NetworkInformation.NetworkChange, Microsoft.SPOT.Net");
        //        if (networkChangeType != null)
        //        {
        //            System.Reflection.MethodInfo onNetworkChangeCallbackMethod = networkChangeType.GetMethod("OnNetworkChangeCallback", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        //            onNetworkChangeCallbackMethod.Invoke(networkChangeType, new object[] { networkEvent });
        //        }
        //    }
        //}

        EthernetInterface _ethernetInterface;
        bool _isDisposed = false;

        ArpResolver _arpResolver;

        const byte MAX_SIMULTANEOUS_SOCKETS = 8; /* must be between 2 and 64; one socket is reserved for background UDP operations such as the DHCP and DNS clients */
        static Netduino.IP.Socket[] _sockets = new Netduino.IP.Socket[MAX_SIMULTANEOUS_SOCKETS];
        UInt64 _handlesInUseBitmask = 0;
        object _handlesInUseBitmaskLockObject = new object();

        // our IP configuration
        //UInt32 _ipv4configIPAddress = 0xC0A80564;     /* IP: 192.168.5.100 */
        //UInt32 _ipv4configSubnetMask = 0xFFFFFF00;  /* SM: 255.255.255.0 */
        //UInt32 _ipv4configGatewayAddress = 0xC0A80501;     /* GW: 192.168.5.1 */
        UInt32 _ipv4configIPAddress = 0x00000000;     /* IP: 0.0.0.0 = IPAddress.Any */
        UInt32 _ipv4configSubnetMask = 0x00000000;  /* SM: 0.0.0.0 = IPAddress.Any */
        UInt32 _ipv4configGatewayAddress = 0x00000000;     /* GW: 0.0.0.0 = IPAddress.Any */
        UInt32[] _ipv4configDnsAddresses = new UInt32[0];

        const UInt32 LOOPBACK_IP_ADDRESS = 0x7F000001;
        const UInt32 LOOPBACK_SUBNET_MASK = 0xFF000000;

        const Int32 MAX_IPV4_DATA_FRAGMENT_SIZE = 1500 - IPV4_HEADER_MIN_LENGTH; /* max IPv4 data fragment size */

        const Int32 LOOPBACK_BUFFER_SIZE = MAX_IPV4_DATA_FRAGMENT_SIZE; /* max IPv4 payload size */
        UInt32 _loopbackSourceIPAddress = 0;
        UInt32 _loopbackDestinationIPAddress = 0;
        ProtocolType _loopbackProtocol = (ProtocolType)0;
        byte[] _loopbackBuffer = null;
        const Int32 _loopbackBufferIndex = 0;
        Int32 _loopbackBufferCount = 0;
        bool _loopbackBufferInUse = false;
        AutoResetEvent _loopbackBufferFreedEvent = new AutoResetEvent(false);
        object _loopbackBufferLockObject = new object();
        System.Threading.Thread _loopbackThread;
        AutoResetEvent _loopbackBufferFilledEvent = new AutoResetEvent(false);

        //// fixed buffer for IPv4 header
        const int IPV4_HEADER_MIN_LENGTH = 20;
        const int IPV4_HEADER_MAX_LENGTH = 60;
        byte[] _ipv4HeaderBuffer = new byte[IPV4_HEADER_MAX_LENGTH];
        object _ipv4HeaderBufferLockObject = new object();

        const int MAX_BUFFER_SEGMENT_COUNT = 3;
        byte[][] _bufferArray = new byte[MAX_BUFFER_SEGMENT_COUNT][];
        int[] _indexArray = new int[MAX_BUFFER_SEGMENT_COUNT];
        int[] _countArray = new int[MAX_BUFFER_SEGMENT_COUNT];

        UInt16 _nextDatagramID = 0;
        object _nextDatagramIDLockObject = new object();

        const UInt16 FIRST_EPHEMERAL_PORT = 0xC000;
        UInt16 _nextEphemeralPort = FIRST_EPHEMERAL_PORT;
        object _nextEphemeralPortLockObject = new object();

        const byte DEFAULT_TIME_TO_LIVE = 64; /* default TTL recommended by RFC 1122 */

        public enum ProtocolType : byte
        {
            Udp = 17,   // user datagram protocol
        }

        public IPv4Layer(EthernetInterface ethernetInterface)
        {
            // save a reference to our Ethernet; we'll use this to push IPv4 frames onto the Ethernet interface
            _ethernetInterface = ethernetInterface;

            // create and configure my ARP resolver; the ARP resolver will automatically wire itself up to receiving incoming ARP frames
            _arpResolver = new ArpResolver(ethernetInterface);

            // retrieve our IP address configuration from the config sector and configure ARP
            object networkInterface = Netduino.IP.Interop.NetworkInterface.GetNetworkInterface(0);
            bool dhcpEnabled = (bool)networkInterface.GetType().GetMethod("get_IsDhcpEnabled").Invoke(networkInterface, new object[] { });

            // configure our ARP resolver's default IP address settings
            if (dhcpEnabled)
            {
                // in case of DHCP, temporarily set our IP address to IP_ADDRESS_ANY (0.0.0.0)
                _arpResolver.SetIpv4Address(0);
            }
            else
            {
                _ipv4configIPAddress = ConvertIPAddressStringToUInt32BE((string)networkInterface.GetType().GetMethod("get_IPAddress").Invoke(networkInterface, new object[] { }));
                _ipv4configSubnetMask = ConvertIPAddressStringToUInt32BE((string)networkInterface.GetType().GetMethod("get_SubnetMask").Invoke(networkInterface, new object[] { }));
                _ipv4configGatewayAddress = ConvertIPAddressStringToUInt32BE((string)networkInterface.GetType().GetMethod("get_GatewayAddress").Invoke(networkInterface, new object[] { }));
                string[] dnsAddressesString = (string[])networkInterface.GetType().GetMethod("get_DnsAddresses").Invoke(networkInterface, new object[] { });
                _ipv4configDnsAddresses = new UInt32[dnsAddressesString.Length];
                for (int iDnsAddress = 0; iDnsAddress < _ipv4configDnsAddresses.Length; iDnsAddress++)
                {
                    _ipv4configDnsAddresses[iDnsAddress] = ConvertIPAddressStringToUInt32BE(dnsAddressesString[iDnsAddress]);
                }
                _arpResolver.SetIpv4Address(_ipv4configIPAddress);
            }

            // wire up our IPv4PacketReceived handler
            _ethernetInterface.IPv4PacketReceived += _ethernetInterface_IPv4PacketReceived;
            // wire up our LinkStateChanged event handler
            _ethernetInterface.LinkStateChanged += _ethernetInterface_LinkStateChanged;

            // start our "loopback thread"
            _loopbackThread = new Thread(LoopbackInBackgroundThread);
            _loopbackThread.Start();

            // manually fire our LinkStateChanged event to set the initial state of our link.
            _ethernetInterface_LinkStateChanged(_ethernetInterface, _ethernetInterface.GetLinkState());
        }

        void _ethernetInterface_LinkStateChanged(object sender, bool state)
        {
            /* TODO: we appear to be sending the gratuitous ARP too quickly upon first link; do we need to wait a few milliseconds before replying on the link to truly be "link up"?
             *       consider adding a "delayUntilTicks" parameter to all ARP background-queued frames (which may also require them to be sorted by time).  
             *       Also...if we are going to include an abritrary delay to our initial gratuitous ARP, perhaps we should do so by delaying the EthernetInterface.LinkStateChanged(true) event in the ILinkLayer driver
             *       (on a per-chip basis, in case the delay is specific to certain MAC/PHY chips' requrirements for startup time rather than a network router requirement */
            if (state == true)
                _arpResolver.SendArpGratuitousInBackground();
        }

        void _ethernetInterface_IPv4PacketReceived(object sender, byte[] buffer, int index, int count)
        {
            // if IPv4 header is less than 20 bytes then drop packet
            if (count < 20) return;

            // if version field is not 4 (IPv4) then drop packet
            if ((buffer[index] >> 4) != 4) return;

            // get header and datagram lengths
            byte headerLength = (byte)((buffer[index] & 0x0F) * 4);
            UInt16 totalLength = (UInt16)((buffer[index + 2] << 8) + buffer[index + 3]);
            // check header checksum; calculating checksum over the entire header--including the checksum value--should result in 0x000.
            UInt16 checksum = Utility.CalculateInternetChecksum(buffer, index, headerLength);
            // if checksum does not match then drop packet
            if (checksum != 0x0000) return;

            /* TODO: process frame ID field (which uniquely identifies each frame), and deal with fragments of those frames; we will not however process datagrams/frames larger than 1536 bytes total. */
            /* NOTE: we will have to cache partial frames in this class until all fragments are received (or timeout occurs) since we may not receive the fragment which indicates which socketHandle is the target until the 2nd or later fragment */
            /* NOTE: we will enable a 30 second timeout PER INCOMING DATAGRAM; if all fragments have not been received before the timeout then we will discard the datagram from our buffers and send an ICMPv4 Time Exceeded (code 1) message;
             *       we do not restart the timeout after every fragment...it is a maximum timeout for the entire datagram.  also note that we can only send the ICMP timeout if we have received frame 0 (with the source port information) */
            //UInt16 identification = (UInt16)((buffer[index + 4] << 8) + buffer[index + 5]);
            //byte flags = (byte)(buffer[index + 6] >> 5);
            //UInt16 fragmentOffset = (UInt16)(((buffer[index + 6] & 0x1F) << 8) + buffer[index + 7]);

            ProtocolType protocol = (ProtocolType)buffer[index + 9];

            // get our source and destination IP addresses
            UInt32 sourceIPAddress = (UInt32)((buffer[index + 12] << 24) + (buffer[index + 13] << 24) + (buffer[index + 14] << 24) + buffer[index + 15]);
            UInt32 destinationIPAddress = (UInt32)((buffer[index + 16] << 24) + (buffer[index + 17] << 24) + (buffer[index + 18] << 24) + buffer[index + 19]);

            CallSocketPacketReceivedHandler(sourceIPAddress, destinationIPAddress, protocol, buffer, index + headerLength, count - headerLength);
        }

        void CallSocketPacketReceivedHandler(UInt32 sourceIPAddress, UInt32 destinationIPAddress, ProtocolType protocol, byte[] buffer, Int32 index, Int32 count)
        {
            /* TODO: find the port # for our packet (looking into the UDP or TCP packet) */
            //UInt16 sourceIPPort = 0;
            UInt16 destinationIPPort = 0;

            /* TODO: replace this socketHandle with the _actual_ socketHandle */
            UInt32 socketHandle = 1;

            switch (protocol)
            {
                case ProtocolType.Udp: /* UDP */
                    {
                        /* TODO: we maybe should manage a pool of "rx buffers" here and allocating them to incoming data on the fly.  we'd also reassemble fragmented packets.  
                         *       the reality is that, optimally, we'd just pass our original buffer all the way to the user...but NETMF's model uses buffers so that the data can be polled for
                         *       and read at leisure. in any case, we must free up (and possibly dereference) the rx buffer when the data has been read by the application */
                        //                        _sockets[socketHandle].OnPacketReceived(buffer, index + headerLength, count - headerLength);
                    }
                    break;
                //case ProtocolType.Tcp:
                //case ProtocolType.Icmp:
                //case ProtocolType.Igmp:
                default:   /* unsupported protocol; drop packet */
                    return;
            }
        }

        /* this function returns a new socket...or null if no socket could be allocated */
        internal Socket CreateSocket(ProtocolType protocolType)
        {
            switch (protocolType)
            {
                case ProtocolType.Udp:
                    {
                        int handle = GetNextHandle();
                        if (handle != -1)
                        {
                            _sockets[handle] = new UdpSocket(this, handle);
                            return _sockets[handle];
                        }
                        else
                        {
                            // no handle available
                            //throw new System.Net.Sockets.SocketException(System.Net.Sockets.SocketError.TooManyOpenSockets);
                            return null;
                        }
                    }
                    break;
                default:
                    throw new NotSupportedException();
            }
        }

        internal Socket GetSocket(int handle)
        {
            return _sockets[handle];
        }

        int GetNextHandle()
        {
            lock (_handlesInUseBitmaskLockObject)
            {
                /* check all available handles from 1 to MAX_SIMULTANEOUS_SOCKETS - 1; handle #0 is reserved for our internal (DHCP, DNS, etc.) use */
                for (int i = 1; i < MAX_SIMULTANEOUS_SOCKETS; i++)
                {
                    if ((_handlesInUseBitmask & ((UInt64)1 << i)) == 0)
                    {
                        _handlesInUseBitmask |= ((UInt64)1 << i);
                        return i;
                    }
                }

                /* if we reach here, there are no free handles. */
                return -1;
            }
        }

        void LoopbackInBackgroundThread()
        {
            while (true)
            {
                _loopbackBufferFilledEvent.WaitOne();

                 // if we have been disposed, shut down our thread now.
                if (_isDisposed)
                    return;

                if (_loopbackBufferInUse)
                {
                    lock (_loopbackBufferLockObject)
                    {
                        // send our loopback frame data to our incoming frame handler
                        CallSocketPacketReceivedHandler(_loopbackSourceIPAddress, _loopbackDestinationIPAddress, _loopbackProtocol, _loopbackBuffer, _loopbackBufferIndex, _loopbackBufferCount);
                        // free our loopback frame
                        _loopbackBufferInUse = false;
                        _loopbackBufferFreedEvent.Set();
                    }
                }
            }
        }

        public void Send(byte protocol, UInt32 srcIPAddress, UInt32 dstIPAddress, byte[][] buffer, int[] offset, int[] count, Int64 timeoutInMachineTicks)
        {
            /* if we are receiving more than (MAX_BUFFER_COUNT - 1) buffers, abort; if we need more, we'll have to change our array sizes at top */
            if (buffer.Length > MAX_BUFFER_SEGMENT_COUNT - 1)
                throw new ArgumentException();

            // determine whether dstIPAddress is a local address or a remote address.
            UInt64 dstPhysicalAddress;
            if ((dstIPAddress == _ipv4configIPAddress) || ((dstIPAddress & LOOPBACK_SUBNET_MASK) == (LOOPBACK_IP_ADDRESS & LOOPBACK_SUBNET_MASK)))
            {
                // loopback: the destination is ourselves

                // if the loopback buffer is in use then wait for it to be freed (or until our timeout occurs); if timeout occrs then drop the packet
                bool loopbackBufferInUse = _loopbackBufferInUse;
                if (loopbackBufferInUse)
                {
                    Int32 waitTimeout = System.Math.Min((Int32)((timeoutInMachineTicks != Int64.MaxValue) ? (timeoutInMachineTicks - Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks) / System.TimeSpan.TicksPerMillisecond : 1000), 1000);
                    if (waitTimeout < 0) waitTimeout = 0;
                    loopbackBufferInUse = !(_loopbackBufferFreedEvent.WaitOne(waitTimeout, false));
                }

                if (!loopbackBufferInUse)
                {
                    lock (_loopbackBufferLockObject)
                    {
                        _loopbackProtocol = (ProtocolType)protocol;
                        _loopbackSourceIPAddress = srcIPAddress;
                        _loopbackDestinationIPAddress = dstIPAddress;

                        // if we haven't needed loopback yet, allocate our loopback buffer now.
                        if (_loopbackBuffer == null)
                            _loopbackBuffer = new byte[LOOPBACK_BUFFER_SIZE];

                        int loopbackBufferCount = 0;
                        for (int iBuffer = 0; iBuffer < buffer.Length; iBuffer++)
                        {
                            Array.Copy(buffer[iBuffer], offset[iBuffer], _loopbackBuffer, loopbackBufferCount, count[iBuffer]);
                            loopbackBufferCount += count[iBuffer];
                        }
                        _loopbackBufferCount = loopbackBufferCount;
                        _loopbackBufferInUse = true;

                        _loopbackBufferFilledEvent.Set();
                    }
                }
                return;
            }
            else if ((dstIPAddress & _ipv4configSubnetMask) == (_ipv4configIPAddress & _ipv4configSubnetMask))
            {
                // direct delivery: this destination address is on our local subnet
                /* get destinationPhysicalAddress of dstIPAddress */
                dstPhysicalAddress = _arpResolver.TranslateIPAddressToPhysicalAddress(dstIPAddress, timeoutInMachineTicks);
            }
            else
            {
                // indirect delivery; send the frame to our gateway instead
                /* get destinationPhysicalAddress of dstIPAddress */
                dstPhysicalAddress = _arpResolver.TranslateIPAddressToPhysicalAddress(_ipv4configGatewayAddress, timeoutInMachineTicks);
            }
            if (dstPhysicalAddress == 0)
                throw new Exception(); // could not resolve address /* TODO: find better exception or return a success/fail as bool from this function */

            lock (_ipv4HeaderBufferLockObject)
            {
                int headerLength = IPV4_HEADER_MIN_LENGTH;
                int dataLength = 0;
                for (int i = 0; i < buffer.Length; i++)
                {
                    dataLength += count[i];
                }

                // we will send the data in fragments if the total data length exceeds 1500 bytes
                Int32 fragmentOffset = 0;
                Int32 fragmentLength;

                /* NOTE: we send the fragment offsets in reverse order so that the destination host has a chance to create the full buffer size before receiving additional fragments */
                if (dataLength > MAX_IPV4_DATA_FRAGMENT_SIZE)
                    fragmentOffset = dataLength - (dataLength % MAX_IPV4_DATA_FRAGMENT_SIZE);
                while (fragmentOffset >= 0)
                {
                    fragmentLength = System.Math.Min(dataLength - fragmentOffset, MAX_IPV4_DATA_FRAGMENT_SIZE);

                    // populate the header fields
                    _ipv4HeaderBuffer[1] = 0; /* leave the DSField/ECN fields blank */
                    UInt16 identification = GetNextDatagramID();
                    _ipv4HeaderBuffer[4] = (byte)((identification >> 8) & 0xFF);
                    _ipv4HeaderBuffer[5] = (byte)(identification & 0xFF);
                    // TODO: populate flags and fragmentation fields, if necessary
                    _ipv4HeaderBuffer[6] = (byte)(
                        (/* (flags << 5) + */ ((fragmentOffset >> 11) & 0xFF))
                        | ((fragmentOffset + fragmentLength == dataLength) ? 0 : 0x20) /* set MF (More Fragments) bit if this is not the only/last fragment in a datagram */
                        ); 
                    _ipv4HeaderBuffer[7] = (byte)((fragmentOffset >> 3) & 0xFF);
                    // populate the TTL (MaxHopCount) and protocol fields
                    _ipv4HeaderBuffer[8] = DEFAULT_TIME_TO_LIVE;
                    _ipv4HeaderBuffer[9] = protocol;
                    // fill in source and destination addresses
                    _ipv4HeaderBuffer[12] = (byte)((srcIPAddress >> 24) & 0xFF);
                    _ipv4HeaderBuffer[13] = (byte)((srcIPAddress >> 16) & 0xFF);
                    _ipv4HeaderBuffer[14] = (byte)((srcIPAddress >> 8) & 0xFF);
                    _ipv4HeaderBuffer[15] = (byte)(srcIPAddress & 0xFF);
                    _ipv4HeaderBuffer[16] = (byte)((dstIPAddress >> 24) & 0xFF);
                    _ipv4HeaderBuffer[17] = (byte)((dstIPAddress >> 16) & 0xFF);
                    _ipv4HeaderBuffer[18] = (byte)((dstIPAddress >> 8) & 0xFF);
                    _ipv4HeaderBuffer[19] = (byte)(dstIPAddress & 0xFF);

                    /* TODO: populate any datagram options */
                    // pseudocode: while (options) { AddOptionAt(_upV4HeaderBuffer[20 + offset]); headerLength += 4 };

                    // insert the length (and header length)
                    _ipv4HeaderBuffer[0] = (byte)((0x04 << 4) /* version: IPv4 */ + (headerLength / 4)) /* Internet Header Length: # of 32-bit words */;
                    _ipv4HeaderBuffer[2] = (byte)(((headerLength + fragmentLength) >> 8) & 0xFF); /* MSB of total datagram length */
                    _ipv4HeaderBuffer[3] = (byte)((headerLength + fragmentLength) & 0xFF);        /* LSB of total datagram length */

                    // finally calculate the header checksum
                    // for checksum calculation purposes, the checksum field must be empty.
                    _ipv4HeaderBuffer[10] = 0;
                    _ipv4HeaderBuffer[11] = 0;
                    UInt16 checksum = Netduino.IP.Utility.CalculateInternetChecksum(_ipv4HeaderBuffer, 0, headerLength);
                    _ipv4HeaderBuffer[10] = (byte)((checksum >> 8) & 0xFF);
                    _ipv4HeaderBuffer[11] = (byte)(checksum & 0xFF);

                    // queue up our buffer arrays
                    _bufferArray[0] = _ipv4HeaderBuffer;
                    _indexArray[0] = 0;
                    _countArray[0] = headerLength;

                    for (int i = 0; i < buffer.Length; i++)
                    {
                        _bufferArray[i + 1] = buffer[i];
                        _indexArray[i + 1] = offset[i];
                        _countArray[i + 1] = count[i];
                    }

                    Int32 totalBufferOffset = 0;
                    Int32 bufferArrayLength = 1; /* we start with index 1, after our IPv4 header */
                    for (int i = 0; i < buffer.Length; i++)
                    {
                        if (totalBufferOffset + count[i] > fragmentOffset)
                        {
                            // add data from this buffer to our set of downstream buffers
                            _bufferArray[bufferArrayLength] = buffer[i];
                            _indexArray[bufferArrayLength] = offset[i] + System.Math.Max(0, (fragmentOffset - totalBufferOffset));
                            _indexArray[bufferArrayLength] = System.Math.Max(count[i] - System.Math.Max(0, (fragmentOffset - totalBufferOffset)), fragmentLength - totalBufferOffset);
                            bufferArrayLength++;
                        }
                        else
                        {
                            // we have not yet reached our fragment point; increment our totalBufferOffset and move to the next buffer.
                        }
                        totalBufferOffset += count[i];

                        // if we have filled our fragment buffer set completely, break out now.
                        if (totalBufferOffset >= fragmentOffset + fragmentLength)
                            break;
                    }

                    // send the datagram (or datagram fragment)
                    _ethernetInterface.Send(dstPhysicalAddress, 0x0800 /* dataType: IPV4 */, srcIPAddress, dstIPAddress, bufferArrayLength, _bufferArray, _indexArray, _countArray, timeoutInMachineTicks);

                    fragmentOffset -= MAX_IPV4_DATA_FRAGMENT_SIZE;
                }
            }
        }

        UInt16 GetNextDatagramID()
        {
            lock (_nextDatagramIDLockObject)
            {
                return _nextDatagramID++;
            }
        }

        internal UInt16 GetNextEphemeralPortNumber(ProtocolType protocolType)
        {
            UInt16 nextEphemeralPort = 0;
            bool foundAvailablePort = false;
            while (!foundAvailablePort)
            {
                lock (_nextEphemeralPortLockObject)
                {
                    nextEphemeralPort = _nextEphemeralPort++;
                    // if we have wrapped around, then reset to the first ephemeral port
                    if (_nextEphemeralPort == 0)
                        _nextEphemeralPort = FIRST_EPHEMERAL_PORT;
                    foundAvailablePort = true; /* default to "port is available" */
                }

                /* NOTE: for purposes of ephemeral ports, we do not distinguish between multiple potential IP addresses assigned to this NIC. */
                // check and make sure we're not already using this port # on another socket (although ports used on TCP can be re-used on UDP, etc.)
                foreach (Socket socket in _sockets)
                {
                    if (socket.SourceIPPort == nextEphemeralPort && socket.ProtocolType == protocolType)
                        foundAvailablePort = false;
                }
            }

            return nextEphemeralPort;
        }

        static UInt32 ConvertIPAddressStringToUInt32BE(string ipAddress)
        {
            if (ipAddress == null)
                throw new ArgumentNullException();

            ulong ipAddressValue = 0;
            int lastIndex = 0;
            int shiftIndex = 24;
            ulong mask = 0x00000000FF000000;
            ulong octet = 0L;
            int length = ipAddress.Length;

            for (int i = 0; i < length; ++i)
            {
                // Parse to '.' or end of IP address
                if (ipAddress[i] == '.' || i == length - 1)
                    // If the IP starts with a '.'
                    // or a segment is longer than 3 characters or shiftIndex > last bit position throw.
                    if (i == 0 || i - lastIndex > 3 || shiftIndex > 24)
                    {
                        throw new ArgumentException();
                    }
                    else
                    {
                        i = i == length - 1 ? ++i : i;
                        octet = (ulong)(ConvertStringToInt32(ipAddress.Substring(lastIndex, i - lastIndex)) & 0x00000000000000FF);
                        ipAddressValue = ipAddressValue + (ulong)((octet << shiftIndex) & mask);
                        lastIndex = i + 1;
                        shiftIndex = shiftIndex - 8;
                        mask = (mask >> 8);
                    }
            }

            return (uint)ipAddressValue;
        }

        static int ConvertStringToInt32(string value)
        {
            char[] num = value.ToCharArray();
            int result = 0;

            bool isNegative = false;
            int signIndex = 0;

            if (num[0] == '-')
            {
                isNegative = true;
                signIndex = 1;
            }
            else if (num[0] == '+')
            {
                signIndex = 1;
            }

            int exp = 1;
            for (int i = num.Length - 1; i >= signIndex; i--)
            {
                if (num[i] < '0' || num[i] > '9')
                {
                    throw new ArgumentException();
                }

                result += ((num[i] - '0') * exp);
                exp *= 10;
            }

            return (isNegative) ? (-1 * result) : result;
        }

        internal UInt32 IPAddress
        {
            get
            {
                return _ipv4configIPAddress;
            }
        }

        internal UInt32 SubnetMask
        {
            get
            {
                return _ipv4configSubnetMask;
            }
        }

        internal UInt32 GatewayAddress
        {
            get
            {
                return _ipv4configGatewayAddress;
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;

            _isDisposed = true;

            // shut down our loopback thread
            if (_loopbackBufferFilledEvent != null)
            {
                _loopbackBufferFilledEvent.Set();
                _loopbackBufferFilledEvent = null;
            }

            _ethernetInterface = null;
            _ipv4HeaderBuffer = null;
            _ipv4HeaderBufferLockObject = null;

            _bufferArray = null;
            _indexArray = null;
            _countArray = null;
        }
    }
}