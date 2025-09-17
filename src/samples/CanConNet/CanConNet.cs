// SPDX-License-Identifier: MIT
//----------------------------------------------------------------------------
// Summary  : Demo application for the IXXAT VCI .NET-API with TCP server.
//            This demo demonstrates the following VCI features
//              - adapter selection
//              - controller initialization
//              - creation of a message channel
//              - transmission/reception of CAN messages
//              - forwarding received CAN messages to a TCP client (Unity)
//----------------------------------------------------------------------------

using Ixxat.Vci4;
using Ixxat.Vci4.Bal;
using Ixxat.Vci4.Bal.Can;
using System;
using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CanConNet
{
    class CanConNet
    {
        #region Member variables

        static IVciDevice? mDevice;
        static ICanControl? mCanCtl;
        static ICanChannel? mCanChn;
        static ICanScheduler? mCanSched;
        static ICanMessageWriter? mWriter;
        static ICanMessageReader? mReader;
        static Thread? rxThread;
        static long mMustQuit = 0;
        static AutoResetEvent? mRxEvent;

        // TCP server
        static TcpListener? server;
        static TcpClient? client;
        static NetworkStream? stream;
        static bool isServerInitialized = false;

        #endregion

        #region Application entry point

        static void Main(string[] args)
        {
            Console.WriteLine("Server started...");
            Console.WriteLine(" >>>> VCI.NET - API Example V1.1 with TCP <<<<");

            // Start TCP server in background
            StartServer();

            Console.WriteLine(" Select Adapter...");
            if (SelectDevice())
            {
                Console.WriteLine(" Select Adapter.......... OK !");
                Console.WriteLine(" Initialize CAN...");

                if (!InitSocket(0))
                {
                    Console.WriteLine(" Initialize CAN............ FAILED !");
                }
                else
                {
                    Console.WriteLine(" Initialize CAN............ OK !");

                    rxThread = new Thread(new ThreadStart(ReceiveThreadFunc));
                    rxThread.Start();

                    ICanCyclicTXMsg? cyclicMsg = null;
                    if (null != mCanSched)
                    {
                        cyclicMsg = mCanSched.AddMessage();
                        cyclicMsg.AutoIncrementMode = CanCyclicTXIncMode.NoInc;
                        cyclicMsg.Identifier = 200;
                        cyclicMsg.CycleTicks = 100;
                        cyclicMsg.DataLength = 8;
                        cyclicMsg.SelfReceptionRequest = true;

                        for (Byte i = 0; i < cyclicMsg.DataLength; i++)
                        {
                            cyclicMsg[i] = i;
                        }
                    }

                    ConsoleKeyInfo cki = new ConsoleKeyInfo();
                    Console.WriteLine(" Press T to transmit single message.");
                    if (null != mCanSched)
                        Console.WriteLine(" Press C to start/stop cyclic message.");
                    else
                        Console.WriteLine(" Cyclic messages not supported.");
                    Console.WriteLine(" Press ESC to exit.");

                    do
                    {
                        while (!Console.KeyAvailable)
                        {
                            Thread.Sleep(10);
                        }
                        cki = Console.ReadKey(true);
                        if (cki.Key == ConsoleKey.T)
                        {
                            uint transmittedDataId = 0x100;

                            byte[] canDataBytePack = Enumerable.Range(0, 8).Select(i => (byte)i).ToArray();

                            TransmitData(transmittedDataId, canDataBytePack);

                            // Send to TCP client if connected
                            if (isServerInitialized && client != null && client.Connected && stream != null)
                            {
                                try
                                {
                                    CanJsonMessage jsonMessage = new CanJsonMessage
                                    {
                                        Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                        ID = transmittedDataId,
                                        DLC = canDataBytePack.Length,
                                        Data = canDataBytePack
                                    };

                                    byte[] data = Encoding.UTF8.GetBytes(jsonMessage.ToJson() +"\n");

                                    string canDataHexString  = BitConverter.ToString(canDataBytePack).Replace("-",":");

                                    stream.Write(data, 0, data.Length);

                                    Console.WriteLine($"Data send to Unity: Time: {jsonMessage.Time} - ID:{jsonMessage.ID} - DLC:{jsonMessage.DLC} - Data:{canDataHexString}");
                                }
                                catch (Exception e)
                                {
                                    Console.WriteLine("Error sending to TCP client: " + e.Message);
                                }
                            }
                        }
                        else if (cki.Key == ConsoleKey.C)
                        {
                            if (null != cyclicMsg)
                            {
                                if (cyclicMsg.Status != CanCyclicTXStatus.Busy)
                                    cyclicMsg.Start(0);
                                else
                                    cyclicMsg.Stop();
                            }
                        }
                    } while (cki.Key != ConsoleKey.Escape);

                    if (null != cyclicMsg)
                        cyclicMsg.Stop();

                    Interlocked.Exchange(ref mMustQuit, 1);
                    rxThread.Join();
                }

                Console.WriteLine(" Free VCI - Resources...");
                FinalizeApp();
                Console.WriteLine(" Free VCI - Resources........ OK !");
            }

            Console.WriteLine(" Done");
            Console.ReadLine();
        }

        #endregion

        #region TCP Server

        static void StartServer()
        {
            if (isServerInitialized) return;

            server = new TcpListener(IPAddress.Loopback, 5000);
            server.Start();
            Console.WriteLine("TCP Server started on port 5000...");

            // Run accept loop in background
            Task.Run(() =>
            {
                while (true)
                {
                    try
                    {
                        var tcpClient = server.AcceptTcpClient(); // blocking but safe here
                        Console.WriteLine("Client connected!");

                        client = tcpClient;
                        stream = client.GetStream();
                        isServerInitialized = true;

                        // Start Unity -> Console receive loop
                        Task.Run(() =>
                        {
                            try
                            {
                                byte[] buffer = new byte[1024];
                                while (client != null && client.Connected && stream != null)
                                {
                                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                                    if (bytesRead > 0)
                                    {
                                        string unityJsonMessage = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                                        Console.WriteLine($"Unity json message received: {unityJsonMessage}");

                                        CanJsonMessage canJsonMessage = CanJsonMessage.FromJson(unityJsonMessage);

                                        HandleUnityCanMessage(canJsonMessage);

                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("Error receiving from Unity: " + ex.Message);
                                Console.WriteLine("Error receiving from Unity Stack Trace: " + ex.StackTrace);
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Server error: " + ex.Message);
                    }
                }
            });
        }

        #endregion

        #region Unity -> CAN message handler

        static void HandleUnityCanMessage(CanJsonMessage unityJsonMessage)
        {
            try
            {
                Console.WriteLine($"Received from Unity: ID={unityJsonMessage.ID:X}, DLC={unityJsonMessage.DLC}, Data=[{string.Join(", ", unityJsonMessage.Data ?? new byte[0])}]");
                TransmitData((uint)unityJsonMessage.ID, unityJsonMessage.Data ?? new byte[0]);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error handling Unity message: " + ex.Message);
                Console.WriteLine("Error handling Unity Stack Trace: " + ex.StackTrace);
            }
        }


        #endregion

        #region Message transmission

        static void TransmitData(uint id, byte[] payload)
        {
            if (null == mWriter) return;

            IMessageFactory factory = VciServer.Instance()!.MsgFactory;
            ICanMessage canMsg = (ICanMessage)factory.CreateMsg(typeof(ICanMessage));

            canMsg.TimeStamp = 0;
            canMsg.Identifier = id;
            canMsg.FrameType = CanMsgFrameType.Data;
            canMsg.DataLength = (byte)payload.Length;
            canMsg.SelfReceptionRequest = true;

            for (int i = 0; i < payload.Length; i++)
                canMsg[i] = payload[i];

            mWriter.SendMessage(canMsg);
        }

        #endregion

        #region Device selection

        static bool SelectDevice()
        {
            bool succeeded = false;
            IVciDeviceManager? deviceManager = null;
            IVciDeviceList? deviceList = null;
            IEnumerator? deviceEnum = null;

            try
            {
                deviceManager = VciServer.Instance()!.DeviceManager;
                deviceList = deviceManager.GetDeviceList();
                deviceEnum = deviceList.GetEnumerator();
                deviceEnum.MoveNext();
                mDevice = deviceEnum.Current as IVciDevice;

                if (null != mDevice)
                {
                    IVciCtrlInfo? info = mDevice.Equipment[0];
                    Console.WriteLine(" BusType    : {0}", info.BusType);
                    Console.WriteLine(" CtrlType   : {0}", info.ControllerType);

                    object serialNumberGuid = mDevice.UniqueHardwareId;
                    string serialNumberText = GetSerialNumberText(ref serialNumberGuid);
                    Console.WriteLine(" Interface    : " + mDevice.Description);
                    Console.WriteLine(" Serial number: " + serialNumberText);

                    succeeded = true;
                }
            }
            catch (Exception exc)
            {
                Console.WriteLine("Error: " + exc.Message);
            }
            finally
            {
                DisposeVciObject(deviceManager);
                DisposeVciObject(deviceList);
                DisposeVciObject(deviceEnum);
            }

            return succeeded;
        }

        #endregion

        #region Opening socket

        static bool InitSocket(Byte canNo)
        {
            IBalObject? bal = null;
            bool succeeded = false;

            if (null == mDevice)
                return false;

            try
            {
                bal = mDevice.OpenBusAccessLayer();
                mCanChn = bal.OpenSocket(canNo, typeof(ICanChannel)) as ICanChannel;

                if (null != mCanChn)
                {
                    if (mCanChn.Features.HasFlag(CanFeatures.Scheduler))
                    {
                        mCanSched = bal.OpenSocket(canNo, typeof(ICanScheduler)) as ICanScheduler;
                        if (null != mCanSched)
                        {
                            mCanSched.Reset();
                            mCanSched.Resume();
                        }
                    }

                    mCanChn.Initialize(1024, 128, false);
                    mReader = mCanChn.GetMessageReader();
                    mReader.Threshold = 1;

                    mRxEvent = new AutoResetEvent(false);
                    mReader.AssignEvent(mRxEvent);

                    mWriter = mCanChn.GetMessageWriter();
                    mWriter.Threshold = 1;
                    mCanChn.Activate();

                    mCanCtl = bal.OpenSocket(canNo, typeof(ICanControl)) as ICanControl;
                    if (null != mCanCtl)
                    {
                        mCanCtl.InitLine(CanOperatingModes.Standard |
                          CanOperatingModes.Extended |
                          CanOperatingModes.ErrFrame,
                          CanBitrate.Cia500KBit);

                        Console.WriteLine(" LineStatus: {0}", mCanCtl.LineStatus);

                        mCanCtl.SetAccFilter(CanFilter.Std,
                                             (uint)CanAccCode.All, (uint)CanAccMask.All);
                        mCanCtl.SetAccFilter(CanFilter.Ext,
                                             (uint)CanAccCode.All, (uint)CanAccMask.All);
                        mCanCtl.StartLine();
                        succeeded = true;
                    }
                }
            }
            catch (Exception exc)
            {
                Console.WriteLine("Error: Initializing socket failed : " + exc.Message);
                succeeded = false;
            }
            finally
            {
                DisposeVciObject(bal);
            }

            return succeeded;
        }

        #endregion

        #region Message reception

        static void PrintMessage(ICanMessage canMessage)
        {
            byte[] canData = new byte[canMessage.DataLength];

            for (int index = 0; index < canMessage.DataLength; index++)
            {
                canData[index] = canMessage[index];
            }

            switch (canMessage.FrameType)
            {
                case CanMsgFrameType.Data:
                    if (!canMessage.RemoteTransmissionRequest)
                    {
                        Console.Write("Time: {0,10}  ID: {1,3:X}  DLC: {2,1}  Data:",
                                      canMessage.TimeStamp,
                                      canMessage.Identifier,
                                      canMessage.DataLength);

                        for (int index = 0; index < canMessage.DataLength; index++)
                        {
                            Console.Write(" {0,2:X}", canMessage[index]);
                        }

                        Console.Write("\n");
                    }
                    else
                    {
                        Console.WriteLine("Time: {0,10}  ID: {1,3:X}  DLC: {2,1}  Remote Frame",
                                      canMessage.TimeStamp,
                                      canMessage.Identifier,
                                      canMessage.DataLength);

                        for (int index = 0; index < canMessage.DataLength; index++)
                        {
                            Console.Write(" {0,2:X}", canMessage[index]);
                        }
                    }
                    break;

                case CanMsgFrameType.Info:
                    switch ((CanMsgInfoValue)canMessage[0])
                    {
                        case CanMsgInfoValue.Start:
                            Console.WriteLine("CAN started...");
                            break;
                        case CanMsgInfoValue.Stop:
                            Console.WriteLine("CAN stopped...");
                            break;
                        case CanMsgInfoValue.Reset:
                            Console.WriteLine("CAN reset...");
                            break;
                    }
                    break;

                case CanMsgFrameType.Error:
                    switch ((CanMsgError)canMessage[0])
                    {
                        case CanMsgError.Stuff:
                            Console.WriteLine("stuff error...");
                            break;
                        case CanMsgError.Form:
                            Console.WriteLine("form error...");
                            break;
                        case CanMsgError.Acknowledge:
                            Console.WriteLine("acknowledgment error...");
                            break;
                        case CanMsgError.Bit:
                            Console.WriteLine("bit error...");
                            break;
                        case CanMsgError.Fdb:
                            Console.WriteLine("fast data bit error...");
                            break;
                        case CanMsgError.Crc:
                            Console.WriteLine("CRC error...");
                            break;
                        case CanMsgError.Dlc:
                            Console.WriteLine("Data length error...");
                            break;
                        case CanMsgError.Other:
                            Console.WriteLine("other error...");
                            break;
                    }
                    break;
            }
        }

        static void ReadMsgsViaReadMessage()
        {
            if ((null == mReader) || (null == mRxEvent)) return;

            ICanMessage canMessage;

            do
            {
                if (mRxEvent.WaitOne(100, false))
                {
                    while (mReader.ReadMessage(out canMessage))
                    {
                        PrintMessage(canMessage);
                    }
                }
            } while (0 == mMustQuit);
        }

        static void ReceiveThreadFunc()
        {
            ReadMsgsViaReadMessage();
        }

        #endregion

        #region Utility methods

        static string GetSerialNumberText(ref object serialNumberGuid)
        {
            string resultText;

            if (serialNumberGuid.GetType() == typeof(System.Guid))
            {
                System.Guid tempGuid = (System.Guid)serialNumberGuid;
                byte[] byteArray = tempGuid.ToByteArray();

                if (((char)byteArray[0] == 'H') && ((char)byteArray[1] == 'W'))
                {
                    resultText = "";
                    int i = 0;
                    while (true)
                    {
                        if (byteArray[i] != 0)
                            resultText += (char)byteArray[i];
                        else
                            break;
                        i++;
                        if (i == byteArray.Length) break;
                    }
                }
                else
                {
                    resultText = serialNumberGuid.ToString() ?? "<error getting serial>";
                }
            }
            else
            {
                string tempString = (string)serialNumberGuid;
                resultText = "";
                for (int i = 0; i < tempString.Length; i++)
                {
                    if (tempString[i] != 0)
                        resultText += tempString[i];
                    else
                        break;
                }
            }
            return resultText;
        }

        static void FinalizeApp()
        {
            DisposeVciObject(mReader);
            DisposeVciObject(mWriter);
            if (null != mCanSched) DisposeVciObject(mCanSched);
            DisposeVciObject(mCanChn);
            DisposeVciObject(mCanCtl);
            DisposeVciObject(mDevice);
        }

        static void DisposeVciObject(object? obj)
        {
            if (null != obj)
            {
                IDisposable? dispose = obj as IDisposable;
                if (null != dispose)
                {
                    dispose.Dispose();
                    obj = null;
                }
            }
        }

        #endregion
    }
}
