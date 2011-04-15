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
using System.Runtime.InteropServices;
using System.Security;

namespace SlimThreading {

	[SuppressUnmanagedCodeSecurity]
	internal static class NativeMethods {

		//
		// Structure used with the GetSystemInfo service.
		//

		[StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEM_INFO {
			internal int oemId;
			internal int pageSize;
			internal IntPtr minimumApplicationAddress;
			internal IntPtr maximumApplicationAddress;
			internal IntPtr activeProcessorMask;
			internal int numberOfProcessors;
			internal int processorType;
			internal int allocationGranularity;
			internal short processorLevel;
			internal short processorRevision;
		}

        //
        // Native methods.
        //

		[DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

		[return: MarshalAs(UnmanagedType.Bool)]
		[DllImport("kernel32.dll")]
        internal static extern bool GetProcessAffinityMask(IntPtr processHandle,
														   out UIntPtr processAffinityMask,
														   out UIntPtr systemAffinityMask);

		[DllImport("kernel32.dll")]
        internal static extern void GetSystemInfo(ref SYSTEM_INFO systemInfo);

		[DllImport("kernel32.dll")]
        internal static extern int SwitchToThread();
	}
}