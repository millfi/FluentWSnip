using System.Runtime.InteropServices;

namespace WSnip.Windows;

/// <summary>The native save dialog returns its filter index, including distinct PNG/HDR PNG choices.</summary>
public static unsafe class WindowsFilePicker
{
    public sealed record FileType(string Label, string Extension);
    public sealed record SaveResult(string Path, int FilterIndex);
    [StructLayout(LayoutKind.Sequential)]
    private struct FilterSpec { public nint Name; public nint Spec; }
    public static SaveResult? Save(nint owner, string title, string name, string folder, IReadOnlyList<FileType> types)
    {
        Guid clsid = new("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B"), iid = new("84BCCD23-5FDE-4CDB-AEA4-AF64B83D78AB");
        nint dialog = 0, item = 0, directory = 0;
        var strings = new List<nint>();
        nint Text(string value) { nint p = Marshal.StringToCoTaskMemUni(value); strings.Add(p); return p; }
        try
        {
            Marshal.ThrowExceptionForHR(CoCreateInstance(&clsid, 0, 1, &iid, &dialog));
            var filters = types.Select(t => new FilterSpec { Name = Text(t.Label), Spec = Text("*" + t.Extension) }).ToArray();
            fixed (FilterSpec* p = filters)
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, FilterSpec*, int>)Slot(dialog, 4))(dialog, (uint)filters.Length, p));
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(dialog, 5))(dialog, 1));
            // Overwrite prompt, strict file types, filesystem items, and an existing parent folder.
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(dialog, 9))(dialog, 0x2 | 0x4 | 0x40 | 0x800));
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(dialog, 15))(dialog, Text(name)));
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(dialog, 17))(dialog, Text(title)));
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(dialog, 22))(dialog, Text(types[0].Extension.TrimStart('.'))));
            Guid shellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            if (Directory.Exists(folder) && SHCreateItemFromParsingName(Text(folder), 0, &shellItem, &directory) >= 0)
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(dialog, 12))(dialog, directory));
            int hr = ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(dialog, 3))(dialog, owner);
            if (hr == unchecked((int)0x800704C7)) return null;
            Marshal.ThrowExceptionForHR(hr);
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(dialog, 20))(dialog, &item));
            nint path = 0;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(item, 5))(item, 0x80058000, &path));
            strings.Add(path);
            uint index = 1;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(dialog, 6))(dialog, &index));
            return new SaveResult(Marshal.PtrToStringUni(path)!, checked((int)index - 1));
        }
        finally
        {
            foreach (nint s in strings) Marshal.FreeCoTaskMem(s);
            if (item != 0) Release(item);
            if (directory != 0) Release(directory);
            if (dialog != 0) Release(dialog);
        }
    }
    private static nint Slot(nint obj, int slot) => (*(nint**)obj)[slot];
    private static void Release(nint obj) => ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(obj, 2))(obj);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* obj);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(nint path, nint binding, Guid* iid, nint* item);
}
