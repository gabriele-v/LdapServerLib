﻿using log4net;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Flexinets.Ldap
{
    public class LdapServer
    {
        private readonly ILog _log = LogManager.GetLogger(typeof(LdapServer));
        private readonly TcpListener _server;

        public Boolean Running
        {
            get;
            private set;
        }


        /// <summary>
        /// Create a new server on endpoint
        /// </summary>
        /// <param name="serverEndpoint"></param>
        /// <param name="dictionary"></param>
        public LdapServer(IPEndPoint serverEndpoint)
        {
            _server = new TcpListener(serverEndpoint);
        }


        /// <summary>
        /// Start listening for requests
        /// </summary>
        public void Start()
        {
            _server.Start();
            _server.BeginAcceptTcpClient(ReceiveCallback, null);
            Running = true;
        }


        /// <summary>
        /// Stop listening
        /// </summary>
        public void Stop()
        {
            _server.Stop();
            Running = false;
        }


        /// <summary>
        /// Receive packets
        /// </summary>
        /// <param name="ar"></param>
        private void ReceiveCallback(IAsyncResult ar)
        {
            if (Running)
            {
                var client = _server.EndAcceptTcpClient(ar);
                _server.BeginAcceptTcpClient(ReceiveCallback, null);

                _log.Debug($"Connection from {client.Client.RemoteEndPoint}");

                try
                {
                    var stream = client.GetStream();

                    int i = 0;
                    while (true)
                    {
                        var bytes = new Byte[1024];
                        i = stream.Read(bytes, 0, bytes.Length);
                        if (i == 0)
                        {
                            break;
                        }

                        var data = Encoding.UTF8.GetString(bytes, 0, i);
                        _log.Debug($"Received {i} bytes: {data}");
                        _log.Debug(Utils.ByteArrayToString(bytes));
                        ParseLdapPacket(bytes, i);

                        if (data.Contains("cn=bindUser,cn=Users,dc=dev,dc=company,dc=com"))
                        {
                            var bindresponse = Utils.StringToByteArray("300c02010161070a010004000400"); // bind success...
                            stream.Write(bindresponse, 0, bindresponse.Length);
                        }
                        if (data.Contains("sAMAccountName"))
                        {
                            var searchresponse = Utils.StringToByteArray("300c02010265070a012004000400");   // object not found
                            stream.Write(searchresponse, 0, searchresponse.Length);
                        }
                    }

                    _log.Debug($"Connection closed to {client.Client.RemoteEndPoint}");
                    client.Close();
                }
                catch (IOException ioex)
                {
                    _log.Warn("oops", ioex);
                }
            }
        }


        public class Tag
        {
            private Byte _tagByte;

            public Tag(Byte tagByte)
            {
                _tagByte = tagByte;
            }

            public Boolean IsPrimitive
            {
                get { return !(new BitArray(new byte[] { _tagByte }).Get(5)); }    // todo endianess...
            }

            public TagType TagType
            {
                // todo fix...
                get
                {
                    var foo = new BitArray(new byte[] { _tagByte }).Get(6);
                    var bar = new BitArray(new byte[] { _tagByte }).Get(7);
                    if (!foo && !bar)
                    {
                        return TagType.Universal;
                    }
                    if (bar && !foo)
                    {
                        return TagType.Context;
                    }
                    else
                    {
                        return TagType.Application;
                    }
                }
            }


            public UniversalDataType DataType
            {
                get
                {
                    return (UniversalDataType)GetTagType(_tagByte);
                }
            }


            public LdapOperation LdapOperation
            {
                get
                {
                    return (LdapOperation)GetTagType(_tagByte);
                }
            }


            private Byte GetTagType(Byte tagByte)
            {
                var bits = new BitArray(new byte[] { _tagByte });
                //Trace.WriteLine(BitsToString(bits));
                bits.Set(5, false);
                bits.Set(6, false);
                bits.Set(7, false);
                //Trace.WriteLine(BitsToString(bits));
                byte[] bytes = new byte[1];
                bits.CopyTo(bytes, 0);
                //Trace.WriteLine(bytes[0]);
                return  bytes[0];
            }
        }


        /// <summary>
        /// Used for debugging...
        /// </summary>
        /// <param name="bits"></param>
        /// <returns></returns>
        public static String BitsToString(BitArray bits)
        {
            var derp = "";
            foreach (var bit in bits)
            {
                derp += Convert.ToInt32(bit);
            }
            return derp;
        }


        public enum TagType
        {
            Universal = 0,
            Application = 1,
            Context = 2,
        }


        // Universal data types from https://en.wikipedia.org/wiki/X.690#BER_encoding
        public enum UniversalDataType
        {
            EndOfContent = 0,
            Boolean = 1,
            Integer = 2,
            OctetString = 4,
            Enumerated = 10,
            Sequence = 16,
            // todo add rest if needed...
        }


        // Ldap operations from https://tools.ietf.org/html/rfc4511#section-4.2
        public enum LdapOperation
        {
            BindRequest = 0,
            BindResponse = 1,
            UnbindRequest = 2,
            SearchRequest = 3,
            // todo add rest if needed...
        }


        /// <summary>
        /// Parse a raw ldap packet and return something more useful
        /// </summary>
        /// <param name="packetBytes">Buffer containing packet bytes</param>
        /// <param name="length">Actual length of the packet</param>
        public void ParseLdapPacket(Byte[] packetBytes, int length)
        {
            int i = 0;

            while (i <= length)
            {
                var tag = new Tag(packetBytes[i]);
                i++;

                int attributeLength = 0;
                if (packetBytes[i] >> 7 == 1)    // Long notation
                {
                    var lengthoflengthbytes = packetBytes[i] & 127;
                    var lengthBytes = new Byte[4];
                    Buffer.BlockCopy(packetBytes, i + 1, lengthBytes, 0, lengthoflengthbytes);
                    attributeLength = BitConverter.ToInt32(lengthBytes.Reverse().ToArray(), 0);
                    i += lengthoflengthbytes;
                }
                else // Short notation
                {
                    attributeLength = packetBytes[i] & 127;
                }
                i++;


                if (tag.TagType == TagType.Application)
                {
                    _log.Debug($"Attribute length: {attributeLength}, Tagtype: {tag.TagType}, primitive {tag.IsPrimitive}, operation: {tag.LdapOperation}");
                }
                else if (tag.TagType == TagType.Context)
                {
                    _log.Debug($"Attribute length: {attributeLength}, Tagtype: {tag.TagType}, primitive {tag.IsPrimitive}, context specific ??? profit");
                }
                else
                {
                    _log.Debug($"Attribute length: {attributeLength}, TagType: {tag.TagType}, primitive {tag.IsPrimitive}, datatype: {tag.DataType}");
                }

                if (tag.IsPrimitive && attributeLength > 0)
                {
                    if (tag.TagType == TagType.Universal)
                    {
                        if (tag.DataType == UniversalDataType.Boolean)
                        {
                            _log.Debug(BitConverter.ToBoolean(packetBytes, i));
                        }
                        else if (tag.DataType == UniversalDataType.Integer)
                        {
                            var intbytes = new Byte[4];
                            Buffer.BlockCopy(packetBytes, i, intbytes, 4 - attributeLength, attributeLength);

                            _log.Debug(BitConverter.ToUInt32(intbytes.Reverse().ToArray(), 0));
                        }
                        else
                        {
                            var data = Encoding.UTF8.GetString(packetBytes, i, attributeLength);
                            _log.Debug(data);
                        }
                    }
                    else
                    {
                        var data = Encoding.UTF8.GetString(packetBytes, i, attributeLength);
                        _log.Debug(data);
                    }

                    i += attributeLength;
                }
            }
        }
    }
}