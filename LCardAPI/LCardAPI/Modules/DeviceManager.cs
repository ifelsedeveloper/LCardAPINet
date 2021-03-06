﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LCard.API.Data;
using LCard.API.Data.E2010;
using LCard.API.Interfaces;
using LCard.Core.Poco;
using LusbapiBridgeE2010;
using Newtonsoft.Json;

namespace LCard.API.Modules
{
    public class DeviceManager : IDeviceManager, IDisposable
    {
        public IE2010 mE2010 { get; set; }
        private volatile bool _runDetectionLoop;
        private bool bit0, bit1;
        SensorPoco[]  fileSensorPocos;
        private volatile bool _isCheckingBlockAdapter;
        private volatile bool _isBlockAdapter;
        private static bool _isFirstStart = true;
        private double[] _convertValues = {4.9, 1.9, 1.8, 1.0};
        private double[] _blockAdapterExpectedValues = { 12.234, 5.164, -4.963,  0};
        private readonly double[] blockAdapterValues = new double[4];

        public DeviceManager()
        {
            Sensors = new SensorPoco[4];
            for (int i = 0; i < Sensors.Count(); i++)
            {
                Sensors[i] = new SensorPoco {Name = (i+1) + " Канал"};
            }
        }

        public SensorPoco[] Sensors { get; set; }

        public double[] BlockAdapterValues
        {
            get { return blockAdapterValues; }
        }

        public bool IsBlockAdapter
        {
            get { return _isBlockAdapter; }
            set { _isBlockAdapter = value;  }
        }

        public void StartDetectionLoop()
        {

            lock (this)
            {
                if (!_runDetectionLoop)
                {
                    _runDetectionLoop = true;
                    mE2010.OpenLDevice();
                    var moduleDescription = mE2010.Init();
                    if (moduleDescription.HasValue)
                    {
                        SetDefaultAdcParams(mE2010, moduleDescription.Value);
                    }
                    fileSensorPocos = GetAllSensorsFromConfig().ToArray();
                    long index = 0;

                    Task.Factory.StartNew(() =>
                    {
                        CheckBlockAdapter();

                        if (IsBlockAdapter)
                        {
                            mE2010.StartReadData();
                            mE2010.ENABLE_TTL_OUT(true);
                            mE2010.OnData += OnData;
                            while (_runDetectionLoop)
                            {

                                Thread.Sleep(100);
                                bit0 = (index%4) > 1;
                                bit1 = (index%4)%2 == 1;
                                //remove splitter for channel
                                if (bit0 != true || bit1)
                                {
                                    mE2010.SetDigitalIn(
                                        new[]
                                        {
                                            false, false,
                                            false, false,
                                            false, false,
                                            false, false,
                                            // D9   D10
                                            bit0, bit1,
                                            bit0, bit1,
                                            bit0, bit1,
                                            bit0, bit1
                                        });
                                }

                                for (int i = 0; i < 20; i++)
                                {
                                    if(!_runDetectionLoop) break;
                                    Thread.Sleep(250);
                                }
                                



                                index++;
                            }
                            mE2010.StopReadData();
                        }
                    });
                }
            }
        }

        public void CheckBlockAdapter()
        {
            if (_isFirstStart)
            {
                _isCheckingBlockAdapter = true;
                long index = 0;
                bit0 = true;
                bit1 = true;

                _runDetectionLoop = true;

                mE2010.ENABLE_TTL_OUT(true);
                mE2010.OnData += OnData;

                mE2010.SetDigitalIn(
                    new[]
                    {
                        false, false,
                        false, false,
                        false, false,
                        false, false,
                        // D9   D10
                        bit0, bit1,
                        bit0, bit1,
                        bit0, bit1,
                        bit0, bit1
                    });

                mE2010.StartReadData();
                Thread.Sleep(5000);
                mE2010.OnData -= OnData;
                mE2010.StopReadData();
                _isCheckingBlockAdapter = false;
                _isFirstStart = false;
            }


        }

        public bool IsCheckingBlockAdapter
        {
            get { return _isCheckingBlockAdapter; }
        }

        public void StopDetectionLoop()
        {
            _runDetectionLoop = false;
        }

        public List<SensorPoco> GetAllSensorsFromConfig()
        {
            return JsonConvert.DeserializeObject<List<SensorPoco>>(File.ReadAllText("sensors.txt", Encoding.UTF8));
        }

        public void GetAllLCardSensors()
        {
            
        }

        private static void SetDefaultAdcParams(IE2010 mE2010, M_MODULE_DESCRIPTION_E2010 moduleDescription)
        {
            var ap = mE2010.GET_ADC_PARS();

            int i, j;

            // установим желаемые параметры работы АЦП
            if ((char)moduleDescription.Module.Revision == 'A')
                ap.IsAdcCorrectionEnabled = 0;		// запретим автоматическую корректировку данных на уровне модуля (для Rev.A)
            else
                ap.IsAdcCorrectionEnabled = 1;		// разрешим автоматическую корректировку данных на уровне модуля (для Rev.B и выше)
            ap.SynchroPars.StartSource = (ushort)INT_ADC_START.INT_ADC_START_E2010;						// внутренний старт сбора с АЦП
            //	ap.SynchroPars.StartSource = INT_ADC_START_WITH_TRANS_E2010;		// внутренний старт сбора с АЦП с трансляцией
            //	ap.SynchroPars.StartSource = EXT_ADC_START_ON_RISING_EDGE_E2010;	// внешний старт сбора с АЦП по фронту 
            ap.SynchroPars.SynhroSource = (ushort)INT_ADC_CLOCK.INT_ADC_CLOCK_E2010;						// внутренние тактовые импульсы АЦП
            //	ap.SynchroPars.SynhroSource = INT_ADC_CLOCK_WITH_TRANS_E2010;		// внутренние тактовые импульсы АЦП с трансляцией
            ap.SynchroPars.StartDelay = 0x0;									// задержка начала сбора данных в кадрах отсчётов (для Rev.B и выше)
            ap.SynchroPars.StopAfterNKadrs = 0x0;							// останов сбора данных через заданное кол-во кадров отсчётов (для Rev.B и выше)
            ap.SynchroPars.SynchroAdMode = (ushort)E2010_SYNC.NO_ANALOG_SYNCHRO_E2010;	// тип аналоговой синхронизации (для Rev.B и выше)
            //	ap.SynchroPars.SynchroAdMode = ANALOG_SYNCHRO_ON_HIGH_LEVEL_E2010;
            ap.SynchroPars.SynchroAdChannel = 0x0;							// канал аналоговой синхронизации (для Rev.B и выше)
            ap.SynchroPars.SynchroAdPorog = 0;								// порог аналоговой синхронизации в кодах АЦП (для Rev.B и выше)
            ap.SynchroPars.IsBlockDataMarkerEnabled = 0x0;				// маркирование начала блока данных (удобно, например, при аналоговой синхронизации ввода по уровню) (для Rev.B и выше)
            ap.ChannelsQuantity = MODULE_CONSTANTS.ADC_CHANNELS_QUANTITY_E2010; 		// кол-во активных каналов
            // формируем управляющую таблицу 
            ap.ControlTable = new ushort[MODULE_CONSTANTS.MAX_CONTROL_TABLE_LENGTH_E2010];
            for (i = 0x0; i < ap.ChannelsQuantity; i++)
            {
                ap.ControlTable[i] = (ushort)i;
            }
            // частоту сбора будем устанавливать в зависимости от скорости USB
            // частота работы АЦП в кГц
            const double AdcRate = 1000.0;
            ap.AdcRate = AdcRate;
            int DataStep;
            // частота работы АЦП в кГц
            var usbSpeed = mE2010.GetUsbSpeed();
            if (usbSpeed == LusbSpeed.USB11_LUSBAPI)
            {
                ap.InterKadrDelay = 0.01;		// межкадровая задержка в мс
                DataStep = 256 * 1024;				// размер запроса
            }
            else
            {
                ap.InterKadrDelay = 0.0;		// межкадровая задержка в мс
                DataStep = 1024 * 1024;			// размер запроса
            }
            // сконфигурим входные каналы
            ap.InputRange = new ushort[MODULE_CONSTANTS.ADC_CHANNELS_QUANTITY_E2010];
            ap.InputSwitch = new ushort[MODULE_CONSTANTS.ADC_CHANNELS_QUANTITY_E2010];
            for (i = 0x0; i < MODULE_CONSTANTS.ADC_CHANNELS_QUANTITY_E2010; i++)
            {
                ap.InputRange[i] = (ushort)ADC_INPUTV.ADC_INPUT_RANGE_3000mV_E2010;  	// входной диапазоне 3В
                ap.InputSwitch[i] = (ushort)ADC_INPUT.ADC_INPUT_SIGNAL_E2010;			// источник входа - сигнал
            }
            // передаём в структуру параметров работы АЦП корректировочные коэффициенты АЦП
            ap.AdcOffsetCoefs = new double[MODULE_CONSTANTS.ADC_INPUT_RANGES_QUANTITY_E2010, MODULE_CONSTANTS.ADC_CHANNELS_QUANTITY_E2010];
            ap.AdcScaleCoefs = new double[MODULE_CONSTANTS.ADC_INPUT_RANGES_QUANTITY_E2010, MODULE_CONSTANTS.ADC_CHANNELS_QUANTITY_E2010];
            for (i = 0x0; i < MODULE_CONSTANTS.ADC_INPUT_RANGES_QUANTITY_E2010; i++)
                for (j = 0x0; j < MODULE_CONSTANTS.ADC_CHANNELS_QUANTITY_E2010; j++)
                {
                    // корректировка смещения
                    ap.AdcOffsetCoefs[i, j] = moduleDescription.Adc.OffsetCalibration[j + i * MODULE_CONSTANTS.ADC_CHANNELS_QUANTITY_E2010];
                    // корректировка масштаба
                    ap.AdcScaleCoefs[i, j] = moduleDescription.Adc.ScaleCalibration[j + i * MODULE_CONSTANTS.ADC_CHANNELS_QUANTITY_E2010];
                }

            // передадим требуемые параметры работы АЦП в модуль
            mE2010.SET_ADC_PARS(ap, DataStep);
        }

        private void OnData(DataPacketPoco dataPacket)
        {
            lock (this)
            {
                if (_isCheckingBlockAdapter)
                    _isBlockAdapter = true;


                for (int nChannel = 0; nChannel < 4; nChannel++)
                {
                    var valueChannel = 0f;
                    for (int dataN = 0; dataN < dataPacket.DataSize / 10; dataN++)
                    {
                        valueChannel += dataPacket.Datas[nChannel, dataN];
                    }
                    valueChannel = valueChannel / (dataPacket.DataSize / 10);

                    if (bit0 == false && bit1)
                    {
                           
                        if (fileSensorPocos.Any(s => Math.Abs(s.ValueIdentification - Convert.ToDecimal(valueChannel)) < 0.068m))
                        {
                            SensorPoco sensorPoco =
                                fileSensorPocos.FirstOrDefault( s => Math.Abs(s.ValueIdentification - Convert.ToDecimal(valueChannel)) < 0.068m);
                            if (sensorPoco != null)
                            {
                                Sensors[nChannel].Name = (nChannel + 1) + " " + sensorPoco.Name;
                                Sensors[nChannel].ValueConvert = sensorPoco.ValueConvert;
                                Sensors[nChannel].ValueDiff = sensorPoco.ValueDiff;
                                Sensors[nChannel].ValueIdentification = sensorPoco.ValueIdentification;
                            }
                        }
                    }

                    if (bit0 && bit1)
                    {
                        var percison = 0.5;
                        BlockAdapterValues[nChannel] = valueChannel * _convertValues[nChannel];
                        Debug.WriteLine(nChannel + " Adapter Value = " + BlockAdapterValues[nChannel]);
                        _isBlockAdapter = _isBlockAdapter && (Math.Abs(BlockAdapterValues[nChannel] - _blockAdapterExpectedValues[nChannel]) < percison);
                    }
                }
                        
                        
            }

            }
        

            public void Dispose()
            {
                mE2010.OnData -= OnData;
            }
    }
}
