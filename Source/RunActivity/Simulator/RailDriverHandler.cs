﻿/// COPYRIGHT 2010 by the Open Rails project.
/// This code is provided to enable you to contribute improvements to the open rails program.  
/// Use of the code for any other purpose or distribution of the code to anyone else
/// is prohibited without specific written permission from admin@openrails.org.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Diagnostics;
using Microsoft.Win32;
using PIEHidDotNet;
using MSTS;

namespace ORTS
{
    /// <summary>
    /// Class to get data from RailDriver and translate it into something useful for UserInput
    /// </summary>
    public class RailDriverHandler : PIEDataHandler, PIEErrorHandler
    {
        PIEDevice Device = null;            // Our RailDriver
        byte[] WriteBuffer = null;          // Buffer for sending data to RailDriver
        bool Active = false;                // True when RailDriver values are used to control player loco
        RailDriverState State = null;       // Interpreted data from RailDriver passed to UserInput
        bool FullRangeThrottle = false;     // True if full range throttle and no dynamic brake, no way to set this at the moment

        // calibration values, defaults for the developer's RailDriver
        float FullReversed = 225;
        float Neutral = 116;
        float FullForward = 60;
        float FullThrottle = 229;
        float ThrottleIdle = 176;
        float DynamicBrake = 42;
        float DynamicBrakeSetup = 119;
        float AutoBrakeRelease = 216;
        float FullAutoBrake = 79;
        float EmergencyBrake = 58;
        float IndependentBrakeRelease = 213;
        float BailOffEngagedRelease = 179;
        float IndependentBrakeFull = 30;
        float BailOffEngagedFull = 209;
        float BailOffDisengagedRelease = 109;
        float BailOffDisengagedFull = 121;
        float Rotary1Position1 = 73;
        float Rotary1Position2 = 135;
        float Rotary1Position3 = 180;
        float Rotary2Position1 = 86;
        float Rotary2Position2 = 145;
        float Rotary2Position3 = 189;

        /// <summary>
        /// Tries to find a RailDriver and initialize it
        /// </summary>
        /// <param name="basePath"></param>
        public RailDriverHandler(string basePath)
        {
            try
            {
                PIEDevice[] devices = PIEHidDotNet.PIEDevice.EnumeratePIE();
                for (int i = 0; i < devices.Length; i++)
                {
                    if (devices[i].HidUsagePage == 0xc && devices[i].Pid == 210)
                    {
                        Device = devices[i];
                        Device.SetupInterface();
                        Device.SetErrorCallback(this);
                        Device.SetDataCallback(this, DataCallbackFilterType.callOnChangedData);
                        WriteBuffer = new byte[Device.WriteLength];
                        State = new RailDriverState();
                        SetLEDs(0x40, 0x40, 0x40);
                        ReadCalibrationData(basePath);
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Device = null;
                Trace.TraceWarning(e.ToString());
            }
        }

        /// <summary>
        /// Data callback, called when RailDriver data is available
        /// </summary>
        /// <param name="data"></param>
        /// <param name="sourceDevice"></param>
        public void HandlePIEHidData(Byte[] data, PIEDevice sourceDevice)
        {
            if (sourceDevice != Device)
                return;
            State.SaveButtonData();
            byte[] rdata = null;
            while (0 == sourceDevice.ReadData(ref rdata)) //do this so don't ever miss any data
            {
#if false
                    String output = "Callback: " + sourceDevice.Pid + ", ID: " + Device.ToString() + ", data=";
                    for (int i = 0; i < sourceDevice.ReadLength; i++)
                        output = output + rdata[i].ToString() + "  ";
                    Console.WriteLine(output);
#endif
                State.DirectionPercent = Percentage(rdata[1], FullReversed, Neutral, FullForward);
                if (FullRangeThrottle)
                {
                    State.ThrottlePercent = Percentage(rdata[2], FullThrottle, DynamicBrake);
                    State.DynamicBrakePercent = -100;
                }
                else
                {
                    State.ThrottlePercent = Percentage(rdata[2], ThrottleIdle, FullThrottle);
                    State.DynamicBrakePercent = Percentage(rdata[2], ThrottleIdle, DynamicBrakeSetup, DynamicBrake);
                }
                State.TrainBrakePercent = Percentage(rdata[3], AutoBrakeRelease, FullAutoBrake);
                State.EngineBrakePercent = Percentage(rdata[4], IndependentBrakeRelease, IndependentBrakeFull);
                float a = .01f * State.EngineBrakePercent;
                float calOff = (1 - a) * BailOffDisengagedRelease + a * BailOffDisengagedFull;
                float calOn = (1 - a) * BailOffEngagedRelease + a * BailOffEngagedFull;
                State.BailOff = Percentage(rdata[5], calOff, calOn) > 50;
                if (State.TrainBrakePercent >= 100)
                    State.Emergency = Percentage(rdata[3], FullAutoBrake, EmergencyBrake) > 50;
                State.Wipers = (int)(.01 * Percentage(rdata[6], Rotary1Position1, Rotary1Position2, Rotary1Position3) + 2.5);
                State.Lights = (int)(.01 * Percentage(rdata[7], Rotary2Position1, Rotary2Position2, Rotary2Position3) + 2.5);
                State.AddButtonData(rdata);
            }
            if (State.IsPressed(4, 0x30))
                State.Emergency = true;
            if (State.IsPressed(1, 0x40))
            {
                Active = !Active;
                EnableSpeaker(Active);
                if (Active)
                {
                    SetLEDs(0x80, 0x80, 0x80);
                    LEDSpeed = -1;
                    UserInput.RDState = State;
                }
                else
                {
                    SetLEDs(0x40, 0x40, 0x40);
                    UserInput.RDState = null;
                }
            }
            State.Changed = true;
        }
        
        /// <summary>
        /// Error callback
        /// </summary>
        /// <param name="error"></param>
        /// <param name="sourceDevice"></param>
        public void HandlePIEHidError(Int32 error, PIEDevice sourceDevice)
        {
            Trace.TraceWarning("RailDriver Error: " + error.ToString());
        }

        float Percentage(float x, float x0, float x100)
        {
            float p= 100 * (x - x0) / (x100 - x0);
            if (p < 5)
                return 0;
            if (p > 95)
                return 100;
            return p;
        }
        float Percentage(float x, float xminus100, float x0, float xplus100)
        {
            float p = 100 * (x - x0) / (xplus100 - x0);
            if (p < 0)
                p = 100 * (x - x0) / (x0 - xminus100);
            if (p < -95)
                return -100;
            if (p > 95)
                return 100;
            return p;
        }

        /// <summary>
        /// Set the RailDriver LEDs to the specified values
        /// led1 is the right most
        /// </summary>
        /// <param name="led1"></param>
        /// <param name="led2"></param>
        /// <param name="led3"></param>
        void SetLEDs(byte led1, byte led2, byte led3)
        {
            if (Device == null)
                return;
            for (int i = 0; i < WriteBuffer.Length; i++)
                WriteBuffer[i] = 0;
            WriteBuffer[1] = 134;
            WriteBuffer[2] = led1;
            WriteBuffer[3] = led2;
            WriteBuffer[4] = led3;
            Device.WriteData(WriteBuffer);
        }

        /// <summary>
        /// Turns raildriver speaker on or off
        /// </summary>
        /// <param name="on"></param>
        void EnableSpeaker(bool on)
        {
            if (Device == null)
                return;
            for (int i = 0; i < WriteBuffer.Length; i++)
                WriteBuffer[i] = 0;
            WriteBuffer[1] = 133;
            WriteBuffer[7] = (byte) (on ? 1 : 0);
            Device.WriteData(WriteBuffer);
        }

        // LED values for digits 0 to 9
        byte[] LEDDigits = { 0x3f, 0x06, 0x5b, 0x4f, 0x66, 0x6d, 0x7d, 0x07, 0x7f, 0x6f };
        // LED values for digits 0 to 9 with decimal point
        byte[] LEDDigitsPoint = { 0xbf, 0x86, 0xdb, 0xcf, 0xe6, 0xed, 0xfd, 0x87, 0xff, 0xef };
        int LEDSpeed = -1;      // speed in tenths displayed on RailDriver LED

        /// <summary>
        /// Updates speed display on RailDriver LED
        /// </summary>
        /// <param name="playerLoco"></param>
        public void Update(TrainCar playerLoco)
        {
            if (!Active || playerLoco == null || Device == null)
                return;
            float speed = 10 * MpS.FromMpS(playerLoco.SpeedMpS, false);
            int s = (int) (speed >= 0 ? speed + .5 : -speed + .5);
            if (s != LEDSpeed)
            {
                if (s < 100)
                    SetLEDs(LEDDigits[s % 10], LEDDigitsPoint[s / 10], 0);
                else if (s < 1000)
                    SetLEDs(LEDDigits[s % 10], LEDDigitsPoint[(s / 10) % 10], LEDDigits[(s / 100) % 10]);
                else if (s < 10000)
                    SetLEDs(LEDDigitsPoint[(s / 10) % 10], LEDDigits[(s / 100) % 10], LEDDigits[(s / 1000) % 10]);
                LEDSpeed = s;
            }
        }

        /// <summary>
        /// Reads RailDriver calibration data from a ModernCalibration.rdm file
        /// This file is not in the usual STF format, but the STFReader can handle it okay.
        /// </summary>
        /// <param name="basePath"></param>
        void ReadCalibrationData(string basePath)
        {
            string file = basePath + "\\ModernCalibration.rdm";
            if (!File.Exists(file))
            {
                RegistryKey RK = Registry.LocalMachine.OpenSubKey("SOFTWARE\\PI Engineering\\PIBUS");
                if (RK != null)
                {
                    string dir = (string)RK.GetValue("RailDriver", null, RegistryValueOptions.None);
                    if (dir != null)
                        file = dir + "\\..\\controller\\ModernCalibration.rdm";
                }
                if (!File.Exists(file))
                {
                    SetLEDs(0, 0, 0);
                    Trace.TraceWarning("Cannot find RailDriver calibration file " + file);
                    return;
                }
            }
            STFReader reader = new STFReader(file, false);
            while (!reader.Eof)
            {
                string token = reader.ReadItem();
                if (token == "Position")
                {
                    string name = reader.ReadItem();
                    int min= -1;
                    int max= -1;
                    while (token != "}")
                    {
                        token = reader.ReadItem();
                        if (token == "Min")
                            min = reader.ReadInt(STFReader.UNITS.Any, -1);
                        else if (token == "Max")
                            max = reader.ReadInt(STFReader.UNITS.Any, -1);
                    }
                    if (min >= 0 && max >= 0)
                    {
                        float v = .5f * (min + max);
                        switch (name)
                        {
                            case "Full Reversed": FullReversed = v; break;
                            case "Neutral": Neutral = v; break;
                            case "Full Forward": FullForward = v; break;
                            case "Full Throttle": FullThrottle = v; break;
                            case "Throttle Idle": ThrottleIdle = v; break;
                            case "Dynamic Brake": DynamicBrake = v; break;
                            case "Dynamic Brake Setup": DynamicBrakeSetup = v; break;
                            case "Auto Brake Released": AutoBrakeRelease = v; break;
                            case "Full Auto Brake (CS)": FullAutoBrake = v; break;
                            case "Emergency Brake (EMG)": EmergencyBrake = v; break;
                            case "Independent Brake Released": IndependentBrakeRelease = v; break;
                            case "Bail Off Engaged (in Released position)": BailOffEngagedRelease = v; break;
                            case "Independent Brake Full": IndependentBrakeFull = v; break;
                            case "Bail Off Engaged (in Full position)": BailOffEngagedFull = v; break;
                            case "Bail Off Disengaged (in Released position)": BailOffDisengagedRelease = v; break;
                            case "Bail Off Disengaged (in Full position)": BailOffDisengagedFull = v; break;
                            case "Rotary Switch 1-Position 1(OFF)": Rotary1Position1 = v; break;
                            case "Rotary Switch 1-Position 2(SLOW)": Rotary1Position2 = v; break;
                            case "Rotary Switch 1-Position 3(FULL)": Rotary1Position3 = v; break;
                            case "Rotary Switch 2-Position 1(OFF)": Rotary2Position1 = v; break;
                            case "Rotary Switch 2-Position 2(DIM)": Rotary2Position2 = v; break;
                            case "Rotary Switch 2-Position 3(FULL)": Rotary2Position3 = v; break;
                            default: STFException.TraceWarning(reader, "unknown calibration value " + name); break;
                        }
                    }
                }
            }
        }

        public void Shutdown()
        {
            if (Device == null)
                return;
            SetLEDs(0, 0, 0);
        }
    }

    /// <summary>
    /// Processed RailDriver data sent to UserInput class
    /// </summary>
    public class RailDriverState
    {
        public bool Changed = false;        // true when data has been changed but not processed by HandleUserInput
        public float DirectionPercent;      // -100 (reverse) to 100 (forward)
        public float ThrottlePercent;       // 0 to 100
        public float DynamicBrakePercent;   // 0 to 100 if active otherwise less than 0
        public float TrainBrakePercent;     // 0 (release) to 100 (CS), does not include emergency
        public float EngineBrakePercent;    // 0 to 100
        public bool BailOff;                // true when bail off pressed
        public bool Emergency;              // true when train brake handle in emergency or E-stop button pressed
        public int Wipers;                  // wiper rotary, 1 off, 2 slow, 3 full
        public int Lights;                  // lights rotary, 1 off, 2 dim, 3 full
        byte[] ButtonData = null;           // latest button data, one bit per button
        byte[] PreviousButtonData = null;
        RailDriverUserCommand[] Commands = null;

        public RailDriverState()
        {
            ButtonData = new byte[6];
            PreviousButtonData = new byte[6];
            Commands = new RailDriverUserCommand[Enum.GetNames(typeof(UserCommands)).Length];
            // top row of blue buttons left to right
            Commands[(int)UserCommands.GameQuit] = new RailDriverUserCommand(0, 0x01);
            Commands[(int)UserCommands.GameSave] = new RailDriverUserCommand(0, 0x02);
            //Commands[(int)UserCommands. F3] = new RailDriverUserCommand(0, 0x04);
            Commands[(int)UserCommands.DisplayTrackMonitorWindow] = new RailDriverUserCommand(0, 0x08);
            //Commands[(int)UserCommands. F6] = new RailDriverUserCommand(0, 0x10);
            //Commands[(int)UserCommands. F7] = new RailDriverUserCommand(0, 0x20);
            Commands[(int)UserCommands.DisplaySwitchWindow] = new RailDriverUserCommand(0, 0x40);
            Commands[(int)UserCommands.DisplayTrainOperationsWindow] = new RailDriverUserCommand(0, 0x80);
            Commands[(int)UserCommands.DisplayNextStationWindow] = new RailDriverUserCommand(1, 0x01);
            //Commands[(int)UserCommands. F11] = new RailDriverUserCommand(1, 0x02);
            //Commands[(int)UserCommands.GameLogger] = new RailDriverUserCommand(1, 0x04);
            Commands[(int)UserCommands.DisplayCompassWindow] = new RailDriverUserCommand(1, 0x08);
            Commands[(int)UserCommands.GameSwitchAhead] = new RailDriverUserCommand(1, 0x10);
            Commands[(int)UserCommands.GameSwitchBehind] = new RailDriverUserCommand(1, 0x20);
            // bottom row of blue buttons left to right
            //Commands[(int)UserCommands.RailDriverOnOff] = new RailDriverUserCommand(1, 0x40); handled elsewhere
            Commands[(int)UserCommands.CameraToggleShowCab] = new RailDriverUserCommand(1, 0x80);
            Commands[(int)UserCommands.CameraCab] = new RailDriverUserCommand(2, 0x01);
            Commands[(int)UserCommands.CameraOutsideFront] = new RailDriverUserCommand(2, 0x02);
            Commands[(int)UserCommands.CameraOutsideRear] = new RailDriverUserCommand(2, 0x04);
            Commands[(int)UserCommands.CameraCarPrevious] = new RailDriverUserCommand(2, 0x08);
            Commands[(int)UserCommands.CameraCarNext] = new RailDriverUserCommand(2, 0x10);
            Commands[(int)UserCommands.CameraTrackside] = new RailDriverUserCommand(2, 0x20);
            Commands[(int)UserCommands.CameraPassenger] = new RailDriverUserCommand(2, 0x40);
            Commands[(int)UserCommands.CameraBrakeman] = new RailDriverUserCommand(2, 0x80);
            //Commands[(int)UserCommands. hide popups] = new RailDriverUserCommand(3, 0x01);
            Commands[(int)UserCommands.DebugResetSignal] = new RailDriverUserCommand(3, 0x02);
            //Commands[(int)UserCommands. load passengers] = new RailDriverUserCommand(3, 0x04);
            //Commands[(int)UserCommands. ok] = new RailDriverUserCommand(3, 0x08);
            // controls to right of blue buttons
            Commands[(int)UserCommands.CameraPanIn] = new RailDriverUserCommand(3, 0x10);
            Commands[(int)UserCommands.CameraPanOut] = new RailDriverUserCommand(3, 0x20);
            Commands[(int)UserCommands.CameraPanUp] = new RailDriverUserCommand(3, 0x40);
            Commands[(int)UserCommands.CameraPanRight] = new RailDriverUserCommand(3, 0x80);
            Commands[(int)UserCommands.CameraPanDown] = new RailDriverUserCommand(4, 0x01);
            Commands[(int)UserCommands.CameraPanLeft] = new RailDriverUserCommand(4, 0x02);
            // buttons on top left
            //Commands[(int)UserCommands. gear shift] = new RailDriverUserCommand(4, 0x04);
            //Commands[(int)UserCommands. gear shift] = new RailDriverUserCommand(4, 0x08);
            //Commands[(int)UserCommands.ControlEmergency] = new RailDriverUserCommand(4, 0x30); handled elsewhere
            //Commands[(int)UserCommands. alerter] = new RailDriverUserCommand(4, 0x40);
            Commands[(int)UserCommands.ControlSander] = new RailDriverUserCommand(4, 0x80);
            Commands[(int)UserCommands.ControlPantographFirst] = new RailDriverUserCommand(5, 0x01);
            Commands[(int)UserCommands.ControlBell] = new RailDriverUserCommand(5, 0x02);
            Commands[(int)UserCommands.ControlHorn] = new RailDriverUserCommand(5, 0x0c);//either of two bits
        }

        /// <summary>
        /// Saves the latest button data and prepares to get new data
        /// </summary>
        public void SaveButtonData()
        {
            for (int i = 0; i < ButtonData.Length; i++)
            {
                PreviousButtonData[i] = ButtonData[i];
                ButtonData[i] = 0;
            }
        }

        /// <summary>
        /// Ors in new button data
        /// </summary>
        /// <param name="data"></param>
        public void AddButtonData(byte[] data)
        {
            for (int i = 0; i < ButtonData.Length; i++)
                ButtonData[i] |= data[i + 8];
        }

        public override string ToString()
        {
            string s= String.Format("{0} {1} {2} {3} {4} {5} {6}", DirectionPercent, ThrottlePercent, DynamicBrakePercent, TrainBrakePercent, EngineBrakePercent, BailOff, Emergency);
            for (int i = 0; i < 6; i++)
                s += " " + ButtonData[i];
            return s;
        }

        public void Handled()
        {
            Changed = false;
        }

        public bool IsPressed(int index, byte mask)
        {
            return (ButtonData[index] & mask) != 0 && (PreviousButtonData[index] & mask) == 0;
        }

		public bool IsPressed(UserCommands command)
		{
			RailDriverUserCommand c = Commands[(int)command];
            if (c == null || Changed == false)
                return false;
			return c.IsButtonDown(ButtonData) && !c.IsButtonDown(PreviousButtonData);
		}

		public bool IsReleased(UserCommands command)
		{
            RailDriverUserCommand c = Commands[(int)command];
            if (c == null || Changed == false)
                return false;
            return !c.IsButtonDown(ButtonData) && c.IsButtonDown(PreviousButtonData);
		}

		public bool IsDown(UserCommands command)
		{
            RailDriverUserCommand c = Commands[(int)command];
            if (c == null)
                return false;
            return c.IsButtonDown(ButtonData);
		}
    }

    public class RailDriverUserCommand
    {
        int Index;
        byte Mask;
        public RailDriverUserCommand(int index, byte mask)
        {
            Index = index;
            Mask = mask;
        }

        public bool IsButtonDown(byte[] data)
        {
            return (data[Index] & Mask) != 0;
        }
    }

}
