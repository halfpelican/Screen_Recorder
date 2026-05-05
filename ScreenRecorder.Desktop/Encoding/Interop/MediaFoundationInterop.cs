using System;
using System.Runtime.InteropServices;

namespace ScreenRecorder.Desktop.Encoding.Interop
{
    internal static class MF
    {
        public const int MF_VERSION = 0x00020070;
        public const int MFSTARTUP_FULL = 0;
        public const int MFSTARTUP_LITE = 1;
        public static readonly Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFMediaType_Audio = new Guid("73647561-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormat_H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFAudioFormat_AAC = new Guid("00001610-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFVideoFormat_RGB32 = new Guid("00000016-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFAudioFormat_PCM = new Guid("00000001-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFAudioFormat_Float = new Guid("00000003-0000-0010-8000-00AA00389B71");
        public static readonly Guid MFTranscodeContainerType_MP4 = new Guid("E4A3B6C0-5522-4C11-AE8C-D7E8B2EAF7D6");

        public static readonly Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        public static readonly Guid MF_MT_FRAME_RATE = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        public static readonly Guid MF_MT_INTERLACE_MODE = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        public static readonly Guid MF_MT_AVG_BITRATE = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        public static readonly Guid MF_MT_DEFAULT_STRIDE = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        public static readonly Guid MF_MT_FIXED_SIZE_SAMPLES = new Guid("b8ebefaf-b718-4e04-b0a9-116775e3321b");
        public static readonly Guid MF_MT_SAMPLE_SIZE = new Guid("dad3ab78-1990-408b-bce2-eba673dacc10");
        public static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new Guid("c9173739-5e56-461c-b713-46fb995cb95f");
        public static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        public static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        public static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new Guid("322de230-9eeb-43bd-ab7a-ff412251541d");
        public static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        public static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        public static readonly Guid MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION = new Guid("7632f0e6-9538-4d61-acda-ea29c8c14456");

        [DllImport("Mfplat.dll", ExactSpelling = true)]
        public static extern int MFStartup(int version, int dwFlags);

        [DllImport("Mfplat.dll", ExactSpelling = true)]
        public static extern int MFShutdown();

        [DllImport("Mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMediaType(out IMFMediaType ppMFType);

        [DllImport("Mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateSample(out IMFSample ppIMFSample);

        [DllImport("Mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMemoryBuffer(int cbMaxLength, out IMFMediaBuffer ppBuffer);

        [DllImport("Mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateAttributes(out IMFAttributes ppMFAttributes, uint cInitialSize);

        [DllImport("Mfplat.dll", ExactSpelling = true)]
        public static extern int MFSetAttributeSize(IMFAttributes pAttributes, Guid guidKey, uint unWidth, uint unHeight);

        [DllImport("Mfplat.dll", ExactSpelling = true)]
        public static extern int MFSetAttributeRatio(IMFAttributes pAttributes, Guid guidKey, uint unNumerator, uint unDenominator);

        [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        public static extern int MFCreateSinkWriterFromURL(
            [MarshalAs(UnmanagedType.LPWStr)] string pwszOutputURL,
            IntPtr pByteStream,
            IMFAttributes? pAttributes,
            out IMFSinkWriter ppSinkWriter);
    }

    [ComImport]
    [Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFAttributes
    {
        int GetItem([In] Guid guidKey, [In, Out] ref PropVariant pValue);
        int GetItemType([In] Guid guidKey, out int pType);
        int CompareItem([In] Guid guidKey, [In] ref PropVariant Value, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
        int Compare([MarshalAs(UnmanagedType.Interface)] IMFAttributes pTheirs, int MatchType, [MarshalAs(UnmanagedType.Bool)] out bool pbResult);
        int GetUINT32([In] Guid guidKey, out int punValue);
        int GetUINT64([In] Guid guidKey, out long punValue);
        int GetDouble([In] Guid guidKey, out double pfValue);
        int GetGUID([In] Guid guidKey, out Guid pguidValue);
        int GetStringLength([In] Guid guidKey, out int pcchLength);
        int GetString([In] Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string pwszValue, int cchBufSize, out int pcchLength);
        int GetAllocatedString([In] Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] out string ppwszValue, out int pcchLength);
        int GetBlobSize([In] Guid guidKey, out int pcbBlobSize);
        int GetBlob([In] Guid guidKey, [Out] byte[] pBuf, int cbBufSize, out int pcbBlobSize);
        int GetAllocatedBlob([In] Guid guidKey, out IntPtr ppBuf, out int pcbSize);
        int GetUnknown([In] Guid guidKey, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        int SetItem([In] Guid guidKey, [In] ref PropVariant Value);
        int DeleteItem([In] Guid guidKey);
        int DeleteAllItems();
        int SetUINT32([In] Guid guidKey, int unValue);
        int SetUINT64([In] Guid guidKey, long unValue);
        int SetDouble([In] Guid guidKey, double fValue);
        int SetGUID([In] Guid guidKey, [In] Guid guidValue);
        int SetString([In] Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
        int SetBlob([In] Guid guidKey, [In] byte[] pBuf, int cbBufSize);
        int SetUnknown([In] Guid guidKey, [MarshalAs(UnmanagedType.Interface)] object pUnknown);
        int LockStore();
        int UnlockStore();
        int GetCount(out int pcItems);
        int GetItemByIndex(int unIndex, out Guid pguidKey, [In, Out] ref PropVariant pValue);
        int CopyAllItems([MarshalAs(UnmanagedType.Interface)] IMFAttributes pDest);
    }

    [ComImport]
    [Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaType : IMFAttributes
    {
        int GetMajorType(out Guid pguidMajorType);
        int IsCompressedFormat([MarshalAs(UnmanagedType.Bool)] out bool pfCompressed);
        int IsEqual([MarshalAs(UnmanagedType.Interface)] IMFMediaType pIMediaType, out int pdwFlags);
        int GetRepresentation([In] Guid guidRepresentation, out IntPtr ppvRepresentation);
        int FreeRepresentation([In] Guid guidRepresentation, IntPtr pvRepresentation);
    }

    [ComImport]
    [Guid("045FA593-8799-42B8-BC8D-8968C6453507")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaBuffer
    {
        int Lock(out IntPtr ppbBuffer, out int pcbMaxLength, out int pcbCurrentLength);
        int Unlock();
        int GetCurrentLength(out int pcbCurrentLength);
        int SetCurrentLength(int cbCurrentLength);
        int GetMaxLength(out int pcbMaxLength);
    }

    [ComImport]
    [Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSample : IMFAttributes
    {
        int GetSampleFlags(out int pdwSampleFlags);
        int SetSampleFlags(int dwSampleFlags);
        int GetSampleTime(out long phnsSampleTime);
        int SetSampleTime(long hnsSampleTime);
        int GetSampleDuration(out long phnsSampleDuration);
        int SetSampleDuration(long hnsSampleDuration);
        int GetBufferCount(out int pdwBufferCount);
        int GetBufferByIndex(int dwIndex, out IMFMediaBuffer ppBuffer);
        int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
        int AddBuffer(IMFMediaBuffer pBuffer);
        int RemoveBufferByIndex(int dwIndex);
        int RemoveAllBuffers();
        int GetTotalLength(out int pcbTotalLength);
        int CopyToBuffer(IMFMediaBuffer pBuffer);
    }

    [ComImport]
    [Guid("3137F1CD-FE5E-4805-A5D8-FB477448CB3D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSinkWriter
    {
        int AddStream(IMFMediaType pTargetMediaType, out int pdwStreamIndex);
        int SetInputMediaType(int dwStreamIndex, IMFMediaType pInputMediaType, IMFAttributes? pEncodingParameters);
        int BeginWriting();
        int WriteSample(int dwStreamIndex, IMFSample pSample);
        int SendStreamTick(int dwStreamIndex, long llTimestamp);
        int PlaceMarker(int dwStreamIndex, IntPtr pvContext);
        int NotifyEndOfSegment(int dwStreamIndex);
        int Flush(int dwStreamIndex);
        int Finalize();
        int GetServiceForStream(int dwStreamIndex, [In] ref Guid guidService, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
        int GetStatistics(int dwStreamIndex, out MF_SINK_WRITER_STATISTICS pStats);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MF_SINK_WRITER_STATISTICS
    {
        public int cb;
        public long llLastTimestampReceived;
        public long llLastTimestampEncoded;
        public long llLastTimestampProcessed;
        public long llLastStreamTickReceived;
        public long llLastSinkSampleRequest;
        public long qwNumSamplesReceived;
        public long qwNumSamplesEncoded;
        public long qwNumSamplesProcessed;
        public long qwNumStreamTicksReceived;
        public int dwByteCountQueued;
        public long qwByteCountProcessed;
        public int dwNumOutstandingSinkSampleRequests;
        public int dwAverageSampleRateReceived;
        public int dwAverageSampleRateEncoded;
        public int dwAverageSampleRateProcessed;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr p;
        public int p2;
    }
}
