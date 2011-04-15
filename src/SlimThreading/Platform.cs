// Copyright 2011 Carlos Martins
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//  
using System;
using System.Threading;

namespace SlimThreading {

	public static class Platform {
		private const int REFRESH_INTERVAL_MS = 30000;
		private static DateTime nextRefreshTime = DateTime.MinValue;
		private static int processorCount = 1;

		//
		// Yield processor.
		//

		public static void YieldProcessor() {
            NativeMethods.SwitchToThread();
		}

		//
		// Return true when the current process runs on a single processor machine.
		//

		internal static bool IsSingleProcessor {
			get { return ProcessorCount == 1; }
		}

		//
		// Return true when the current process runs on a multiprocessor machine.
		//

		internal static bool IsMultiProcessor {
			get { return ProcessorCount > 1; }
		}

		internal static int ProcessorCount {
			get {
				if (DateTime.UtcNow.CompareTo(nextRefreshTime) >= 0) {
					UIntPtr processAffinityMask;
					UIntPtr systemAffinityMask;

					//
					// Get the process and system affinity mask.
					//

					NativeMethods.GetProcessAffinityMask(NativeMethods.GetCurrentProcess(),
														 out processAffinityMask,
														 out systemAffinityMask);
					//
					// Get the number of processors in the system.
					//

					NativeMethods.SYSTEM_INFO sysInfo = new NativeMethods.SYSTEM_INFO();
					NativeMethods.GetSystemInfo(ref sysInfo);

					//
					// Compute the number of processors used by the
					// current process.
					//

					ulong processMask = (~0UL >> (64 - sysInfo.numberOfProcessors)) &
										 processAffinityMask.ToUInt64();
					int count = 0;
					while (processMask > 0) {
						count++;
						processMask &= processMask - 1;
					}
					processorCount = count;
					nextRefreshTime = DateTime.UtcNow.AddMilliseconds(REFRESH_INTERVAL_MS);
				}
				return processorCount;
			}
		}

        //
        // Adjusts the timeout value based on lastTime.
        //

        public static int AdjustTimeout(ref int lastTime, ref int timeout) {
            if (timeout == Timeout.Infinite) {
                return Timeout.Infinite;
            }
            int now = Environment.TickCount;
            int e = (now == lastTime) ? 1 : now - lastTime;
            if (timeout <= e) {
                return (timeout = 0);
            }
            lastTime = now;
            return (timeout -= e);
        }

        //
        // ...
        //

        public static void SpinWait(int count) {
            Thread.SpinWait(count);
        }
	}
}
