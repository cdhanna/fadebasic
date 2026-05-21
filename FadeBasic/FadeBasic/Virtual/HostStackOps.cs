using System;

namespace FadeBasic.Virtual
{
    // Stack mutation primitives used by runtime hosts when a library
    // command returned a placeholder value via the cooperative pump
    // (HostBridge.PostMessage + SuspendVm) and the host now has the
    // real answer to deposit.
    //
    // These are shared between the web runtime (FadeBasic.Export.Web)
    // and any future host. They live here, in core, because the swap
    // logic is pure VM mechanics — no browser, JSExport, or scheduler
    // state involved. Hosts call these once the wake-up event arrives;
    // tests can call them directly without referencing a host project.
    //
    // Each Swap* assumes the operand stack's top entry IS the placeholder
    // the matching command's executor pushed — i.e. the host has just
    // come back from a HostBridge.SuspendVm-induced pause and the only
    // mutation since the suspend has been popping into this helper.
    public static class HostStackOps
    {
        // Swap the placeholder string pointer on top of the operand
        // stack for a freshly-allocated heap string containing `value`.
        // The placeholder allocation is refcount-decremented (it was
        // a length-0 string allocated by the executor) and the new
        // pointer + STRING type code take its place.
        //
        // Stack layout when entering:
        //   [...prior][8 ptr bytes][typeCode=STRING]
        // Layout when returning:
        //   [...prior][8 new ptr bytes][typeCode=STRING]
        //
        // We pop manually (not via VmUtil.ReadAsVmPtr) because that
        // helper rejects STRING type codes — its TODO comment notes
        // the gap. String allocation matches VmHeap's interned-string
        // layout: 4 bytes per char (UTF-32 in-memory).
        public static bool SwapTopString(VirtualMachine vm, string value)
        {
            if (vm == null) return false;
            value ??= "";

            if (vm.stack.ptr < 9)
            {
                // Not enough bytes on top to be a string placeholder.
                // Surface as a no-op rather than corrupting the stack.
                return false;
            }

            // Pop type byte (intentionally not validated — see helper-
            // level comment about coercion vs. defensive checking),
            // then 8 pointer bytes. PopArraySpan returns a span backed
            // by the stack buffer; VmPtr.FromBytes copies it before
            // anything else can clobber.
            _ = vm.stack.Pop();
            vm.stack.PopArraySpan(8, out var oldPtrSpan);
            var oldPtr = VmPtr.FromBytes(oldPtrSpan);
            vm.heap.TryDecrementRefCount(oldPtr);

            var size = value.Length * 4;
            var span = new byte[size];
            for (var i = 0; i < value.Length; i++)
            {
                var data = (uint)value[i];
                var b = BitConverter.GetBytes(data);
                span[i * 4 + 0] = b[0];
                span[i * 4 + 1] = b[1];
                span[i * 4 + 2] = b[2];
                span[i * 4 + 3] = b[3];
            }
            vm.heap.AllocateString(size, out var newPtr);
            vm.heap.WriteSpan(newPtr, size, span);
            var ptrBytes = VmPtr.GetBytes(ref newPtr);
            VmUtil.PushSpan(ref vm.stack, ptrBytes, TypeCodes.STRING);
            return true;
        }

        // Swap the placeholder primitive (int / real / bool / byte /
        // word / dword / dint / dfloat) on top of the operand stack
        // for `newBytes` (must be exactly TypeCodes.GetByteSize(typeCode)
        // bytes). No heap interaction — the placeholder is overwritten
        // in place. The caller is responsible for passing the right
        // number of bytes for the given type code.
        public static bool SwapTopPrimitive(VirtualMachine vm, byte typeCode, byte[] newBytes)
        {
            if (vm == null) return false;
            if (newBytes == null) return false;
            var size = TypeCodes.GetByteSize(typeCode);
            if (newBytes.Length != size) return false;
            if (vm.stack.ptr < 1 + size) return false;

            // Pop placeholder: type byte + value bytes. We trust the
            // type code on the stack matches what the command's executor
            // pushed; if it doesn't the bug is in the calling host
            // (passed the wrong type for a swap), not something to
            // defensively recover from here.
            vm.stack.ptr -= 1 + size;
            VmUtil.PushSpan(ref vm.stack, newBytes, typeCode);
            return true;
        }
    }
}
