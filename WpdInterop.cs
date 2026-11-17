using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace UsbTransfer
{
    // The method order is the native IUnknown vtable order in PortableDeviceApi.h
    // and PortableDeviceTypes.h. Unused slots keep later methods at their SDK offsets.
    internal static class WpdInterop
    {
        internal static readonly Guid ManagerClass = new Guid("0af10cec-2ecd-4b92-9581-34f6ae0637f3");
        internal static readonly Guid DeviceClass = new Guid("f7c0039a-4762-488a-b4b3-760ef9a1ba9b");
        internal static readonly Guid ValuesClass = new Guid("0c15d503-d017-47ce-9016-7b3f978721cc");
        internal static readonly Guid KeysClass = new Guid("de2d022d-2480-43be-97f0-d1fa2cf98f4f");
        internal static readonly Guid VariantCollectionClass = new Guid("08a99e2f-6d6d-4b80-af5a-baf2bcbe4cb9");
        internal static readonly Guid FolderType = new Guid("27e2e392-a111-48e0-ab0c-e17705a05f85");
        internal static readonly Guid FunctionalType = new Guid("99ed0160-17ff-4c44-9d98-1d7a6f941921");
        internal static readonly Guid StorageCategory = new Guid("23f05bbc-15de-4c2a-a55b-a9af5ce412ef");
        internal static readonly Guid GenericFileType = new Guid("0085e0a6-8d34-45d7-bc5c-447e59c73d48");
        internal static readonly Guid AssociationFormat = new Guid("30010000-ae6c-4804-98ba-c57b46965fe7");
        internal static readonly Guid UnspecifiedFormat = new Guid("30000000-ae6c-4804-98ba-c57b46965fe7");

        private static readonly Guid ObjectProperties = new Guid("ef6b490d-5cd8-437a-affc-da8b60ee4a3c");
        private static readonly Guid ClientProperties = new Guid("204d9f0c-2292-4080-9f42-40664e70f859");
        internal static PropertyKey ParentId = new PropertyKey(ObjectProperties, 3);
        internal static PropertyKey Name = new PropertyKey(ObjectProperties, 4);
        internal static PropertyKey PersistentId = new PropertyKey(ObjectProperties, 5);
        internal static PropertyKey Format = new PropertyKey(ObjectProperties, 6);
        internal static PropertyKey ContentType = new PropertyKey(ObjectProperties, 7);
        internal static PropertyKey Size = new PropertyKey(ObjectProperties, 11);
        internal static PropertyKey OriginalFileName = new PropertyKey(ObjectProperties, 12);
        internal static PropertyKey Created = new PropertyKey(ObjectProperties, 18);
        internal static PropertyKey Modified = new PropertyKey(ObjectProperties, 19);
        internal static PropertyKey FunctionalCategory = new PropertyKey(new Guid("8f052d93-abca-4fc5-a5ac-b01df4dbe598"), 2);
        internal static PropertyKey StorageType = new PropertyKey(new Guid("01a3057a-74d6-4e80-bea7-dc4c212ce50a"), 2);
        internal static PropertyKey DefaultResource = new PropertyKey(new Guid("e81e79be-34f0-41bf-b53f-f1a06ae87842"), 0);
        internal static PropertyKey ClientName = new PropertyKey(ClientProperties, 2);
        internal static PropertyKey ClientMajorVersion = new PropertyKey(ClientProperties, 3);
        internal static PropertyKey ClientMinorVersion = new PropertyKey(ClientProperties, 4);
        internal static PropertyKey ClientRevision = new PropertyKey(ClientProperties, 5);
        internal static PropertyKey ClientDesiredAccess = new PropertyKey(ClientProperties, 9);

        internal const int InsufficientBuffer = unchecked((int)0x8007007A);

        [StructLayout(LayoutKind.Sequential)]
        internal struct PropertyKey
        {
            public Guid FormatId;
            public uint PropertyId;

            public PropertyKey(Guid formatId, uint propertyId)
            {
                FormatId = formatId;
                PropertyId = propertyId;
            }
        }

        // 24 bytes is the size of PROPVARIANT on x64. On x86 Windows only uses the first 16.
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        internal struct PropVariant
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public double OleDate;
            [FieldOffset(8)] public long FileTime;
            [FieldOffset(8)] public IntPtr Pointer;
        }

        [DllImport("ole32.dll")]
        internal static extern int PropVariantClear(ref PropVariant value);

        internal static T Create<T>(Guid classId) where T : class
        {
            return (T)Activator.CreateInstance(Type.GetTypeFromCLSID(classId, true));
        }

        internal static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value))
                Marshal.ReleaseComObject(value);
        }

        internal static string GetString(IntPtr value)
        {
            try { return Marshal.PtrToStringUni(value); }
            finally { Marshal.FreeCoTaskMem(value); }
        }

        [ComImport, Guid("a1567595-4c2f-4574-a6fa-ecef917b9a40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IDeviceManager
        {
            [PreserveSig] int GetDevices(IntPtr deviceIds, ref uint count);
            void RefreshDeviceList();
            [PreserveSig] int GetDeviceFriendlyName([MarshalAs(UnmanagedType.LPWStr)] string deviceId,
                IntPtr name, ref uint length);
        }

        [ComImport, Guid("625e2df8-6392-4cf0-9ad1-3cfa5f17775c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IDevice
        {
            void Open([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IValues clientInfo);
            void SendCommand(uint flags, IValues parameters, out IValues result);
            void Content(out IContent content);
            void Capabilities(out IntPtr capabilities);
            void Cancel();
            void Close();
        }

        [ComImport, Guid("6a96ed84-7c73-4480-9938-bf5af477d426"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IContent
        {
            void EnumObjects(uint flags, [MarshalAs(UnmanagedType.LPWStr)] string parentId, IValues filter,
                out IObjectEnumerator enumerator);
            void Properties(out IProperties properties);
            void Transfer(out IResources resources);
            void CreateObjectWithPropertiesOnly(IValues properties, out IntPtr objectId);
            void CreateObjectWithPropertiesAndData(IValues properties, out IStream stream, ref uint bufferSize,
                IntPtr cookie);
            void Delete(uint options, IVariantCollection objectIds, IntPtr results);
            void GetObjectIDsFromPersistentUniqueIDs(IVariantCollection persistentIds, out IVariantCollection objectIds);
        }

        [ComImport, Guid("10ece955-cf41-4728-bfa0-41eedf1bbf19"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IObjectEnumerator
        {
            [PreserveSig] int Next(uint count, IntPtr output, out uint fetched);
        }

        [ComImport, Guid("7f6d695c-03df-4439-a809-59266beee3a6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IProperties
        {
            void GetSupportedProperties([MarshalAs(UnmanagedType.LPWStr)] string objectId, out IKeyCollection keys);
            void GetPropertyAttributes([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key,
                out IValues attributes);
            void GetValues([MarshalAs(UnmanagedType.LPWStr)] string objectId, IKeyCollection keys, out IValues values);
        }

        [ComImport, Guid("fd8878ac-d841-4d17-891c-e6829cdb6934"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IResources
        {
            void GetSupportedResources([MarshalAs(UnmanagedType.LPWStr)] string objectId, out IKeyCollection keys);
            void GetResourceAttributes([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key,
                out IValues attributes);
            void GetStream([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key, uint mode,
                ref uint bufferSize, out IStream stream);
        }

        [ComImport, Guid("dada2357-e0ad-492e-98db-dd61c53ba353"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IKeyCollection
        {
            void GetCount(out uint count);
            void GetAt(uint index, out PropertyKey key);
            void Add(ref PropertyKey key);
        }

        [ComImport, Guid("89b2e422-4f1b-4316-bcef-a44afea83eb3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IVariantCollection
        {
            void GetCount(out uint count);
            void GetAt(uint index, out PropVariant value);
            void Add(ref PropVariant value);
        }

        [ComImport, Guid("6848f6f2-3155-4f86-b6f5-263eeeab3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IValues
        {
            void GetCount(out uint count);
            void GetAt(uint index, out PropertyKey key, out PropVariant value);
            void SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            void SetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);
            [PreserveSig] int GetStringValue(ref PropertyKey key, out IntPtr value);
            void SetUnsignedIntegerValue(ref PropertyKey key, uint value);
            [PreserveSig] int GetUnsignedIntegerValue(ref PropertyKey key, out uint value);
            void SetSignedIntegerValue(ref PropertyKey key, int value);
            void GetSignedIntegerValue(ref PropertyKey key, out int value);
            void SetUnsignedLargeIntegerValue(ref PropertyKey key, ulong value);
            [PreserveSig] int GetUnsignedLargeIntegerValue(ref PropertyKey key, out ulong value);
            void SetSignedLargeIntegerValue(ref PropertyKey key, long value);
            void GetSignedLargeIntegerValue(ref PropertyKey key, out long value);
            void SetFloatValue(ref PropertyKey key, float value);
            void GetFloatValue(ref PropertyKey key, out float value);
            void SetErrorValue(ref PropertyKey key, int value);
            void GetErrorValue(ref PropertyKey key, out int value);
            void SetKeyValue(ref PropertyKey key, ref PropertyKey value);
            void GetKeyValue(ref PropertyKey key, out PropertyKey value);
            void SetBoolValue(ref PropertyKey key, int value);
            void GetBoolValue(ref PropertyKey key, out int value);
            void SetIUnknownValue(ref PropertyKey key, IntPtr value);
            void GetIUnknownValue(ref PropertyKey key, out IntPtr value);
            void SetGuidValue(ref PropertyKey key, ref Guid value);
            [PreserveSig] int GetGuidValue(ref PropertyKey key, out Guid value);
        }
    }
}
