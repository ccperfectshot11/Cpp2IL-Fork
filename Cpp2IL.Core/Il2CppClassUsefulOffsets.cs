using System;
using System.Collections.Generic;
using System.Linq;

namespace Cpp2IL.Core;

// Offseturile de 64 de biti de mai jos nu mai sunt ghicite din cod masina: sunt calculate din
// `typedef struct Il2CppClass` (il2cpp-class-internals.h:378) din sursa runtime-ului IL2CPP, versiunea
// exacta a jocului, 2021.3.25f1. Asezarea pe x64, cu pointeri de 8 octeti si Il2CppType de 16 (un union
// de 8 plus un cuvant de campuri de biti, aliniat la 8):
//
//   0x00 image, 0x08 gc_desc, 0x10 name, 0x18 namespaze, 0x20 byval_arg, 0x30 this_arg,
//   0x40 element_class, 0x48 castClass, 0x50 declaringType, 0x58 parent, 0x60 generic_class,
//   0x68 typeMetadataHandle, 0x70 interopData, 0x78 klass, 0x80 fields, 0x88 events,
//   0x90 properties, 0x98 methods, 0xA0 nestedTypes, 0xA8 implementedInterfaces,
//   0xB0 interfaceOffsets, 0xB8 static_fields, 0xC0 rgctx_data, 0xC8 typeHierarchy,
//   0xD0 unity_user_data, 0xD8 initializationExceptionGCHandle, 0xDC cctor_started,
//   0xE0 cctor_finished_or_no_cctor, 0xE8 cctor_thread (ALIGN_TYPE(8)),
//   0xF0 genericContainerHandle, 0xF8 instance_size, 0xFC actualSize, 0x100 element_size,
//   0x104 native_size, 0x108 static_fields_size, 0x10C thread_static_fields_size,
//   0x110 thread_static_fields_offset, 0x114 flags, 0x118 token, apoi opt uint16 de la
//   0x11C method_count pana la 0x12A interface_offsets_count, apoi sase uint8 de la
//   0x12C typeHierarchyDepth pana la 0x131 packingSize, apoi cele 15 campuri de un bit in doi
//   octeti, 0x132 si 0x133, si in fine vtable la 0x138.
//
// Doua lucruri pe care sursa le inchide si care erau pana acum presupuneri:
//  - 0x132 bitul 0 este `initialized_and_no_error`, exact ce testeaza ClassInlines::InitFromCodegen
//    (`if (klass->initialized_and_no_error) return klass;`). TODO-ul vechi care banuia 0x135 era gresit,
//    iar CPP2IL_METADIAG spusese deja asta numarand 5.240 de citiri la 0x132 si zero la 0x135.
//  - 0x133 bitul 4 (masca 0x10) este `is_import_or_windows_runtime`, testat in vm::Object::IsInst.
//
// Offseturile de 32 de biti raman neatinse: binarul acestui joc e pe 64 de biti, deci nu am cu ce sa le
// verific pe teren, iar calculul pe hartie da interfaceOffsets la 0x58 si primul octet de biti la 0xBA,
// nu 0x50 si 0xBB. Le las asa tocmai fiindca o constanta schimbata fara masuratoare e chiar greseala pe
// care fisierul asta o repara.
public static class Il2CppClassUsefulOffsets
{
    public const int X86_INTERFACE_OFFSETS_OFFSET = 0x50;
    public const int X86_64_INTERFACE_OFFSETS_OFFSET = 0xB0;

    public static int GetVtableOffset(float metadataVersion, bool is32Bit) =>
        metadataVersion >= 24.2f
            ? is32Bit ? 0x999 /*TODO*/ : 0x138
            : is32Bit ? 0x999 /*TODO*/ : 0x128;

    public static readonly List<UsefulOffset> UsefulOffsets =
    [
        new("cctor_finished", 0x74, typeof(uint), true),
        new("flags1", 0xBB, typeof(byte), true),
        //new UsefulOffset("interface_offsets_count", 0x12A, typeof(ushort), true), //TODO
        // new UsefulOffset("rgctx_data", 0xC0, typeof(IntPtr), true), //TODO
        new("interfaceOffsets", X86_INTERFACE_OFFSETS_OFFSET, typeof(IntPtr), true),
        new("static_fields", 0x5C, typeof(IntPtr), true),
        //new UsefulOffset("vtable", 0x138, typeof(IntPtr), true), //TODO

        //64-bit offsets, derivate din struct - vezi comentariul de deasupra clasei.
        // Cele care lipseau sunt campurile pe care ajutoarele fierbinti ale runtime-ului chiar le citesc:
        // Object::IsInst umbla la byval_arg.type, flags si interopData, iar dispatch-ul de interfata la
        // interfaceOffsets, interface_offsets_count si vtable. Denumirea lor face urmatoarea citire de cod
        // masina mecanica in loc de ghicita.
        new("byval_arg", 0x20, typeof(IntPtr), false),
        new("byval_arg.type", 0x2A, typeof(byte), false),
        new("elementType", 0x40, typeof(IntPtr), false),
        new("castClass", 0x48, typeof(IntPtr), false),
        new("parent", 0x58, typeof(IntPtr), false),
        new("interopData", 0x70, typeof(IntPtr), false),
        new("fields", 0x80, typeof(IntPtr), false),
        new("interfaceOffsets", X86_64_INTERFACE_OFFSETS_OFFSET, typeof(IntPtr), false),
        new("static_fields", 0xB8, typeof(IntPtr), false),
        new("rgctx_data", 0xC0, typeof(IntPtr), false),
        new("typeHierarchy", 0xC8, typeof(IntPtr), false),
        new("cctor_finished", 0xE0, typeof(uint), false),
        new("instance_size", 0xF8, typeof(uint), false),
        new("element_size", 0x100, typeof(uint), false),
        new("flags", 0x114, typeof(uint), false),
        new("token", 0x118, typeof(uint), false),
        new("method_count", 0x11C, typeof(ushort), false),
        new("interfaces_count", 0x128, typeof(ushort), false),
        new("interface_offsets_count", 0x12A, typeof(ushort), false),
        new("typeHierarchyDepth", 0x12C, typeof(byte), false),
        new("rank", 0x12E, typeof(byte), false),
        // 0x132 bitul 0 = initialized_and_no_error; 0x133 bitul 4 (0x10) = is_import_or_windows_runtime
        new("flags1", 0x132, typeof(byte), false),
        new("flags2", 0x133, typeof(byte), false),
        new("vtable", 0x138, typeof(IntPtr), false)
    ];

    public static bool IsStaticFieldsPtr(uint offset, bool is32Bit)
    {
        return GetOffsetName(offset, is32Bit) == "static_fields";
    }

    public static bool IsInterfaceOffsetsPtr(uint offset, bool is32Bit)
    {
        return GetOffsetName(offset, is32Bit) == "interfaceOffsets";
    }

    public static bool IsInterfaceOffsetsCount(uint offset, bool is32Bit)
    {
        return GetOffsetName(offset, is32Bit) == "interface_offsets_count";
    }

    public static bool IsRGCTXDataPtr(uint offset, bool is32Bit)
    {
        return GetOffsetName(offset, is32Bit) == "rgctx_data";
    }

    public static bool IsElementTypePtr(uint offset, bool is32Bit)
    {
        return GetOffsetName(offset, is32Bit) == "elementType";
    }

    public static bool IsPointerIntoVtable(uint offset, float metadataVersion, bool is32Bit)
    {
        return offset >= GetVtableOffset(metadataVersion, is32Bit);
    }

    public static string? GetOffsetName(uint offset, bool is32Bit) =>
        UsefulOffsets.FirstOrDefault(o => o.is32Bit == is32Bit && o.offset == offset)?.name;

    public class UsefulOffset(string name, uint offset, Type type, bool is32Bit)
    {
        public string name = name;
        public uint offset = offset;
        public Type type = type;
        public bool is32Bit = is32Bit;
    }
}
