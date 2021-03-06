﻿using System;
using System.Collections.Generic;
using System.Linq;
using PmcReader.Interop;

namespace PmcReader.AMD
{
    public class Zen2L3Cache : Amd17hCpu
    {
        // ccx -> thread id mapping. Just need one thread per ccx - we'll always sample using that thread
        protected Dictionary<int, int> ccxSampleThreads;
        protected Dictionary<int, List<int>> allCcxThreads;
        public L3CounterData[] ccxCounterData;
        public L3CounterData ccxTotals;

        public Zen2L3Cache()
        {
            architectureName = "Zen 2 L3";
            ccxSampleThreads = new Dictionary<int, int>();
            allCcxThreads = new Dictionary<int, List<int>>();
            for (int threadIdx = 0; threadIdx < GetThreadCount(); threadIdx++)
            {
                int ccxIdx = GetCcxId(threadIdx);
                ccxSampleThreads[ccxIdx] = threadIdx;
                List<int> ccxThreads;
                if (! allCcxThreads.TryGetValue(ccxIdx, out ccxThreads))
                {
                    ccxThreads = new List<int>();
                    allCcxThreads.Add(ccxIdx, ccxThreads);
                }

                ccxThreads.Add(threadIdx);
            }

            monitoringConfigs = new MonitoringConfig[3];
            monitoringConfigs[0] = new HitRateLatencyConfig(this);
            monitoringConfigs[1] = new SliceConfig(this);
            monitoringConfigs[2] = new TestConfig(this);

            ccxCounterData = new L3CounterData[ccxSampleThreads.Count()];
            ccxTotals = new L3CounterData();
        }

        public class L3CounterData
        {
            public float ctr0;
            public float ctr1;
            public float ctr2;
            public float ctr3;
            public float ctr4;
            public float ctr5;
        }

        public void ClearTotals()
        {
            ccxTotals.ctr0 = 0;
            ccxTotals.ctr1 = 0;
            ccxTotals.ctr2 = 0;
            ccxTotals.ctr3 = 0;
            ccxTotals.ctr4 = 0;
            ccxTotals.ctr5 = 0;
        }

        public void UpdateCcxL3CounterData(int ccxIdx, int threadIdx)
        {
            ThreadAffinity.Set(1UL << threadIdx);
            float normalizationFactor = GetNormalizationFactor(threadIdx);
            ulong ctr0 = ReadAndClearMsr(MSR_L3_PERF_CTR_0);
            ulong ctr1 = ReadAndClearMsr(MSR_L3_PERF_CTR_1);
            ulong ctr2 = ReadAndClearMsr(MSR_L3_PERF_CTR_2);
            ulong ctr3 = ReadAndClearMsr(MSR_L3_PERF_CTR_3);
            ulong ctr4 = ReadAndClearMsr(MSR_L3_PERF_CTR_4);
            ulong ctr5 = ReadAndClearMsr(MSR_L3_PERF_CTR_5);

            if (ccxCounterData[ccxIdx] == null) ccxCounterData[ccxIdx] = new L3CounterData();
            ccxCounterData[ccxIdx].ctr0 = ctr0 * normalizationFactor;
            ccxCounterData[ccxIdx].ctr1 = ctr1 * normalizationFactor;
            ccxCounterData[ccxIdx].ctr2 = ctr2 * normalizationFactor;
            ccxCounterData[ccxIdx].ctr3 = ctr3 * normalizationFactor;
            ccxCounterData[ccxIdx].ctr4 = ctr4 * normalizationFactor;
            ccxCounterData[ccxIdx].ctr5 = ctr5 * normalizationFactor;
            ccxTotals.ctr0 += ccxCounterData[ccxIdx].ctr0;
            ccxTotals.ctr1 += ccxCounterData[ccxIdx].ctr1;
            ccxTotals.ctr2 += ccxCounterData[ccxIdx].ctr2;
            ccxTotals.ctr3 += ccxCounterData[ccxIdx].ctr3;
            ccxTotals.ctr4 += ccxCounterData[ccxIdx].ctr4;
            ccxTotals.ctr5 += ccxCounterData[ccxIdx].ctr5;
        }

        public Tuple<string, float>[] GetOverallL3CounterValues(ulong aperf, ulong mperf, ulong irperfcount, ulong tsc,
            string ctr0, string ctr1, string ctr2, string ctr3, string ctr4, string ctr5)
        {
            Tuple<string, float>[] retval = new Tuple<string, float>[10];
            retval[0] = new Tuple<string, float>("APERF", aperf);
            retval[1] = new Tuple<string, float>("MPERF", mperf);
            retval[2] = new Tuple<string, float>("TSC", tsc);
            retval[3] = new Tuple<string, float>("IRPerfCount", irperfcount);
            retval[4] = new Tuple<string, float>(ctr0, ccxTotals.ctr0);
            retval[5] = new Tuple<string, float>(ctr1, ccxTotals.ctr1);
            retval[6] = new Tuple<string, float>(ctr2, ccxTotals.ctr2);
            retval[7] = new Tuple<string, float>(ctr3, ccxTotals.ctr3);
            retval[8] = new Tuple<string, float>(ctr4, ccxTotals.ctr4);
            retval[9] = new Tuple<string, float>(ctr5, ccxTotals.ctr5);
            return retval;
        }

        public class HitRateLatencyConfig : MonitoringConfig
        {
            private Zen2L3Cache l3Cache;

            public HitRateLatencyConfig(Zen2L3Cache l3Cache)
            {
                this.l3Cache = l3Cache;
            }

            public string GetConfigName() { return "Hitrate and Miss Latency"; }
            public string[] GetColumns() { return columns; }
            public void Initialize()
            {
                ulong L3AccessPerfCtl = GetL3PerfCtlValue(0x04, 0xFF, true, 0xF, 0xFF);
                ulong L3MissPerfCtl = GetL3PerfCtlValue(0x04, 0x01, true, 0xF, 0xFF);
                ulong L3MissLatencyCtl = GetL3PerfCtlValue(0x90, 0, true, 0xF, 0xFF);
                ulong L3MissSdpRequestPerfCtl = GetL3PerfCtlValue(0x9A, 0x1F, true, 0xF, 0xFF);

                foreach(KeyValuePair<int, int> ccxThread in l3Cache.ccxSampleThreads)
                {
                    ThreadAffinity.Set(1UL << ccxThread.Value);
                    Ring0.WriteMsr(MSR_L3_PERF_CTL_0, L3AccessPerfCtl);
                    Ring0.WriteMsr(MSR_L3_PERF_CTL_1, L3MissPerfCtl);
                    Ring0.WriteMsr(MSR_L3_PERF_CTL_2, L3MissLatencyCtl);
                    Ring0.WriteMsr(MSR_L3_PERF_CTL_3, L3MissSdpRequestPerfCtl);
                }
            }

            public MonitoringUpdateResults Update()
            {
                MonitoringUpdateResults results = new MonitoringUpdateResults();
                results.unitMetrics = new string[l3Cache.ccxSampleThreads.Count()][];
                float[] ccxClocks = new float[l3Cache.allCcxThreads.Count()];
                l3Cache.ClearTotals();
                ulong totalAperf = 0, totalMperf = 0, totalTsc = 0, totalIrPerfCount = 0;
                foreach (KeyValuePair<int, int> ccxThread in l3Cache.ccxSampleThreads)
                {
                    // Try to determine frequency, by getting max frequency of cores in ccx
                    foreach (int ccxThreadIdx in l3Cache.allCcxThreads[ccxThread.Key])
                    {
                        ThreadAffinity.Set(1UL << ccxThreadIdx);
                        float normalizationFactor = l3Cache.GetNormalizationFactor(l3Cache.GetThreadCount() + ccxThreadIdx);
                        ulong aperf, mperf, tsc, irperfcount;
                        l3Cache.ReadFixedCounters(ccxThreadIdx, out aperf, out irperfcount, out tsc, out mperf);
                        totalAperf += aperf;
                        totalIrPerfCount += irperfcount;
                        totalTsc += tsc;
                        totalMperf += mperf;
                        float clk = tsc * ((float)aperf / mperf) * normalizationFactor;
                        if (clk > ccxClocks[ccxThread.Key]) ccxClocks[ccxThread.Key] = clk;
                        if (ccxThreadIdx == ccxThread.Value)
                        {
                            l3Cache.UpdateCcxL3CounterData(ccxThread.Key, ccxThread.Value);
                            results.unitMetrics[ccxThread.Key] = computeMetrics("CCX " + ccxThread.Key, l3Cache.ccxCounterData[ccxThread.Key], ccxClocks[ccxThread.Key]);
                        }
                    }
                }

                float avgClk = 0;
                foreach (float ccxClock in ccxClocks) avgClk += ccxClock;
                avgClk /= l3Cache.allCcxThreads.Count();
                results.overallMetrics = computeMetrics("Overall", l3Cache.ccxTotals, avgClk);
                results.overallCounterValues = l3Cache.GetOverallL3CounterValues(totalAperf, totalMperf, totalIrPerfCount, totalTsc, 
                    "L3Access", "L3Miss", "L3MissLat/16", "L3MissSdpReq", "Unused", "Unused");
                return results;
            }

            public string[] columns = new string[] { "Item", "Clk", "Hitrate", "Hit BW", "Mem Latency", "Mem Latency?", "Pend. Miss/C", "SDP Requests", "SDP Requests * 64B" };

            public string GetHelpText() { return ""; }

            private string[] computeMetrics(string label, L3CounterData counterData, float clk)
            {
                // event 0x90 counts "total cycles for all transactions divided by 16"
                float ccxL3MissLatency = (float)counterData.ctr2 * 16 / counterData.ctr3;
                float ccxL3Hitrate = (1 - (float)counterData.ctr1 / counterData.ctr0) * 100;
                float ccxL3HitBw = ((float)counterData.ctr0 - counterData.ctr1) * 64;
                return new string[] { label,
                        FormatLargeNumber(clk),
                        string.Format("{0:F2}%", ccxL3Hitrate),
                        FormatLargeNumber(ccxL3HitBw) + "B/s",
                        string.Format("{0:F1} clks", ccxL3MissLatency),
                        string.Format("{0:F1} ns", (1000000000 / clk) * ccxL3MissLatency),
                        string.Format("{0:F2}", counterData.ctr2 * 16 / clk),
                        FormatLargeNumber(counterData.ctr3),
                        FormatLargeNumber(counterData.ctr3 * 64) + "B/s"};
            }
        }

        public class SliceConfig : MonitoringConfig
        {
            private Zen2L3Cache l3Cache;

            public SliceConfig(Zen2L3Cache l3Cache)
            {
                this.l3Cache = l3Cache;
            }

            public string GetConfigName() { return "By the Slice"; }
            public string[] GetColumns() { return columns; }
            public void Initialize()
            {
                ulong L3AccessPerfCtl = GetL3PerfCtlValue(0x04, 0xFF, true, 0xF, 0xFF);
                ulong L3MissPerfCtl = GetL3PerfCtlValue(0x04, 0x01, true, 0xF, 0xFF);
                ulong slice0Lookups = GetL3PerfCtlValue(0x04, 0xFF, true, 0x1, 0xFF);
                ulong slice1Lookups = GetL3PerfCtlValue(0x04, 0xFF, true, 0x2, 0xFF);
                ulong slice2Lookups = GetL3PerfCtlValue(0x04, 0xFF, true, 0x4, 0xFF);
                ulong slice3Lookups = GetL3PerfCtlValue(0x04, 0xFF, true, 0x8, 0xFF);

                foreach (KeyValuePair<int, int> ccxThread in l3Cache.ccxSampleThreads)
                {
                    ThreadAffinity.Set(1UL << ccxThread.Value);
                    Ring0.WriteMsr(MSR_L3_PERF_CTL_0, L3AccessPerfCtl);
                    Ring0.WriteMsr(MSR_L3_PERF_CTL_1, L3MissPerfCtl);
                    Ring0.WriteMsr(MSR_L3_PERF_CTL_2, slice0Lookups);
                    Ring0.WriteMsr(MSR_L3_PERF_CTL_3, slice1Lookups);
                    Ring0.WriteMsr(MSR_L3_PERF_CTL_4, slice2Lookups);
                    Ring0.WriteMsr(MSR_L3_PERF_CTL_5, slice3Lookups);
                }
            }

            public MonitoringUpdateResults Update()
            {
                MonitoringUpdateResults results = new MonitoringUpdateResults();
                results.unitMetrics = new string[l3Cache.ccxSampleThreads.Count()][];
                l3Cache.ClearTotals();
                foreach (KeyValuePair<int, int> ccxThread in l3Cache.ccxSampleThreads)
                {
                    l3Cache.UpdateCcxL3CounterData(ccxThread.Key, ccxThread.Value);
                    results.unitMetrics[ccxThread.Key] = computeMetrics("CCX " + ccxThread.Key, l3Cache.ccxCounterData[ccxThread.Key]);
                }

                results.overallMetrics = computeMetrics("Overall", l3Cache.ccxTotals);
                return results;
            }

            public string[] columns = new string[] { "Item", "Hitrate", "Hit BW", "Slice 0", "Slice 1", "Slice 2", "Slice 3" };

            public string GetHelpText() { return ""; }

            private string[] computeMetrics(string label, L3CounterData counterData)
            {
                float ccxL3Hitrate = (1 - counterData.ctr1 / counterData.ctr0) * 100;
                float ccxL3HitBw = (counterData.ctr0 - counterData.ctr1) * 64;
                return new string[] { label,
                        string.Format("{0:F2}%", ccxL3Hitrate),
                        FormatLargeNumber(ccxL3HitBw) + "B/s",
                        string.Format("{0:F2}%", 100 * counterData.ctr2 / counterData.ctr0),
                        string.Format("{0:F2}%", 100 * counterData.ctr3 / counterData.ctr0),
                        string.Format("{0:F2}%", 100 * counterData.ctr4 / counterData.ctr0),
                        string.Format("{0:F2}%", 100 * counterData.ctr5 / counterData.ctr0)};
            }
        }

        public class TestConfig : MonitoringConfig
        {
            private Zen2L3Cache l3Cache;

            public TestConfig(Zen2L3Cache l3Cache)
            {
                this.l3Cache = l3Cache;
            }

            public string GetConfigName() { return "L3 Lookup Test"; }
            public string[] GetColumns() { return columns; }
            public void Initialize()
            {
                ulong L3MissPerfCtl = GetL3PerfCtlValue(0x04, 0x01, true, 0xF, 0xFF);
                ulong L3Access2 = GetL3PerfCtlValue(0x04, 0x02, true, 0xF, 0xFF);
                ulong L3Access4 = GetL3PerfCtlValue(0x04, 0x04, true, 0xF, 0xFF);
                ulong L3Access8 = GetL3PerfCtlValue(0x04, 0x08, true, 0xF, 0xFF);
                ulong L3Access10 = GetL3PerfCtlValue(0x04, 0x10, true, 0xF, 0xFF);
                ulong L3AccessE0 = GetL3PerfCtlValue(0x04, 0xE0, true, 0xF, 0xFF);

                foreach (KeyValuePair<int, int> ccxThread in l3Cache.ccxSampleThreads)
                {
                    ThreadAffinity.Set(1UL << ccxThread.Value);
                    Ring0.WriteMsr(MSR_L3_PERF_CTL_0, L3MissPerfCtl);
                    Ring0.WriteMsr(MSR_L3_PERF_CTL_1, L3Access2);
                    Ring0.WriteMsr(MSR_L3_PERF_CTL_2, L3Access4);
                    Ring0.WriteMsr(MSR_L3_PERF_CTL_3, L3Access8);
                    Ring0.WriteMsr(MSR_L3_PERF_CTL_4, L3Access10);
                    Ring0.WriteMsr(MSR_L3_PERF_CTL_5, L3AccessE0);
                }
            }

            public MonitoringUpdateResults Update()
            {
                MonitoringUpdateResults results = new MonitoringUpdateResults();
                results.unitMetrics = new string[l3Cache.ccxSampleThreads.Count()][];
                l3Cache.ClearTotals();
                foreach (KeyValuePair<int, int> ccxThread in l3Cache.ccxSampleThreads)
                {
                    l3Cache.UpdateCcxL3CounterData(ccxThread.Key, ccxThread.Value);
                    results.unitMetrics[ccxThread.Key] = computeMetrics("CCX " + ccxThread.Key, l3Cache.ccxCounterData[ccxThread.Key]);
                }

                results.overallMetrics = computeMetrics("Overall", l3Cache.ccxTotals);
                return results;
            }

            public string[] columns = new string[] { "Item", "Hitrate", "Hit BW", "Lookup 0x1 (Miss)", "Lookup 0x2", "Lookup 0x4", "Lookup 0x8", "Lookup 0x10", "Lookup 0xE0" };

            public string GetHelpText() { return ""; }

            private string[] computeMetrics(string label, L3CounterData counterData)
            {
                // event 0x90 counts "total cycles for all transactions divided by 16"
                float l3Access = counterData.ctr0 + counterData.ctr1 + counterData.ctr2 + counterData.ctr3 + counterData.ctr4 + counterData.ctr5;
                float l3Miss = counterData.ctr0;
                float ccxL3Hitrate = (1 - l3Miss / l3Access) * 100;
                float ccxL3HitBw = (l3Access - l3Miss) * 64;
                return new string[] { label,
                        string.Format("{0:F2}%", ccxL3Hitrate),
                        FormatLargeNumber(ccxL3HitBw) + "B/s",
                        FormatLargeNumber(counterData.ctr0),
                        FormatLargeNumber(counterData.ctr1),
                        FormatLargeNumber(counterData.ctr2),
                        FormatLargeNumber(counterData.ctr3),
                        FormatLargeNumber(counterData.ctr4),
                        FormatLargeNumber(counterData.ctr5)};
            }
        }
    }
}
