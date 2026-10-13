#pragma once

// ================================================================================
// IL2CPP metadata API - the offset-free way to reach game code.
//
// Every function here is resolved BY NAME: GetProcAddress on GameAssembly.dll first,
// then UnityPlayerScan::find_il2cpp_api as a fallback, because this build can ship
// with its exports stripped (that is why Il2cppResolution.h exists at all).
//
// ---------------------------------------------------------------------------
// TIMING - the thing that broke this twice. Read before calling anything here.
// ---------------------------------------------------------------------------
// A resolved export is NOT a usable runtime. InitThread waits for GameAssembly.dll
// to be LOADED and then runs Resolve()/Patch() straight away, which is long before
// il2cpp_init has finished. Calling domain_get / class_from_name /
// domain_get_assemblies in that window is undefined: it returns null at best and
// faults at worst, and sweeping ~300 assemblies there reliably crashed the game a
// few seconds after launch.
//
// So: nothing in this file may be called from inject time. Use RuntimeReady() to
// gate, and do metadata work from the deferred worker in main.cpp. Every runtime
// call below is SEH-guarded as a second line of defence - a fault in the game's
// runtime must never take the process down with it.
// ================================================================================

#include <windows.h>
#include <cstdint>
#include <iostream>
#include "../../Utils/globals.h"
#include "Il2cppResolution.h"   // UnityPlayerScan::find_il2cpp_api
#include "Console.h"

namespace Il2CppApi {

    // ---- raw exports -----------------------------------------------------------

    using domain_get_t              = void* (*)();
    using domain_assembly_open_t    = void* (*)(void* domain, const char* name);
    using domain_get_assemblies_t   = void** (*)(void* domain, size_t* count);
    using assembly_get_image_t      = void* (*)(void* assembly);
    using class_from_name_t         = void* (*)(void* image, const char* ns, const char* name);
    using class_get_method_t        = void* (*)(void* klass, const char* name, int argc);
    using class_get_parent_t        = void* (*)(void* klass);
    using class_get_name_t          = const char* (*)(void* klass);
    using class_get_fields_t        = void* (*)(void* klass, void** iter);
    using field_get_name_t          = const char* (*)(void* field);
    using field_get_offset_t        = size_t(*)(void* field);
    using field_get_type_t          = void* (*)(void* field);
    using type_get_name_t           = char* (*)(void* type);
    using object_get_class_t        = void* (*)(void* obj);
    using thread_attach_t           = void* (*)(void* domain);
    using thread_current_t          = void* (*)();
    using free_t                    = void  (*)(void*);

    inline domain_get_t            domain_get = nullptr;
    inline domain_assembly_open_t  domain_assembly_open = nullptr;
    inline domain_get_assemblies_t domain_get_assemblies = nullptr;
    inline assembly_get_image_t    assembly_get_image = nullptr;
    inline class_from_name_t       class_from_name = nullptr;
    inline class_get_method_t      class_get_method_from_name = nullptr;
    inline class_get_parent_t      class_get_parent = nullptr;
    inline class_get_name_t        class_get_name = nullptr;
    inline class_get_fields_t      class_get_fields = nullptr;
    inline field_get_name_t        field_get_name = nullptr;
    inline field_get_offset_t      field_get_offset = nullptr;
    inline field_get_type_t        field_get_type = nullptr;
    inline type_get_name_t         type_get_name = nullptr;
    inline object_get_class_t      object_get_class = nullptr;
    inline thread_attach_t         thread_attach = nullptr;
    inline thread_current_t        thread_current = nullptr;
    inline free_t                  il2cpp_free = nullptr;

    inline bool g_exportsResolved = false;
    inline bool g_reported = false;

    // Why the last readiness/attach check failed, for display. Empty on success.
    inline const char* g_attachError = "";

    // ---- SEH shims -------------------------------------------------------------
    // Deliberately tiny and free of anything with a destructor, so __try is legal.
    // Never merge these into a caller that logs - that is a C2712.

    inline void* SehDomainGet() {
        if (!domain_get) return nullptr;
        __try { return domain_get(); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
    }
    inline void** SehGetAssemblies(void* d, size_t* n) {
        if (!domain_get_assemblies || !d) return nullptr;
        __try { return domain_get_assemblies(d, n); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
    }
    inline void* SehAssemblyImage(void* a) {
        if (!assembly_get_image || !a) return nullptr;
        __try { return assembly_get_image(a); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
    }
    inline void* SehAssemblyOpen(void* d, const char* n) {
        if (!domain_assembly_open || !d) return nullptr;
        __try { return domain_assembly_open(d, n); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
    }
    inline void* SehClassFromName(void* img, const char* ns, const char* n) {
        if (!class_from_name || !img) return nullptr;
        __try { return class_from_name(img, ns, n); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
    }
    inline void* SehGetMethod(void* k, const char* n, int argc) {
        if (!class_get_method_from_name || !k) return nullptr;
        __try { return class_get_method_from_name(k, n, argc); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
    }
    inline void* SehGetParent(void* k) {
        if (!class_get_parent || !k) return nullptr;
        __try { return class_get_parent(k); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
    }
    inline void* SehThreadAttach(void* d) {
        if (!thread_attach || !d) return nullptr;
        __try { return thread_attach(d); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
    }
    inline void* SehThreadCurrent() {
        if (!thread_current) return nullptr;
        __try { return thread_current(); } __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
    }

    // ---- export resolution -----------------------------------------------------
    // Safe at ANY time: this only reads export tables, it never enters the runtime.

    inline bool ResolveExports() {
        if (g_exportsResolved) return true;

        HMODULE ga = GetModuleHandleA("GameAssembly.dll");
        if (!ga) return false;

        auto get = [&](const char* n) -> void* {
            if (void* p = (void*)GetProcAddress(ga, n)) return p;
            return UnityPlayerScan::find_il2cpp_api(n);
        };

        domain_get                 = (domain_get_t)get("il2cpp_domain_get");
        domain_assembly_open       = (domain_assembly_open_t)get("il2cpp_domain_assembly_open");
        domain_get_assemblies      = (domain_get_assemblies_t)get("il2cpp_domain_get_assemblies");
        assembly_get_image         = (assembly_get_image_t)get("il2cpp_assembly_get_image");
        class_from_name            = (class_from_name_t)get("il2cpp_class_from_name");
        class_get_method_from_name = (class_get_method_t)get("il2cpp_class_get_method_from_name");
        class_get_parent           = (class_get_parent_t)get("il2cpp_class_get_parent");
        class_get_name             = (class_get_name_t)get("il2cpp_class_get_name");
        class_get_fields           = (class_get_fields_t)get("il2cpp_class_get_fields");
        field_get_name             = (field_get_name_t)get("il2cpp_field_get_name");
        field_get_offset           = (field_get_offset_t)get("il2cpp_field_get_offset");
        field_get_type             = (field_get_type_t)get("il2cpp_field_get_type");
        type_get_name              = (type_get_name_t)get("il2cpp_type_get_name");
        object_get_class           = (object_get_class_t)get("il2cpp_object_get_class");
        thread_attach              = (thread_attach_t)get("il2cpp_thread_attach");
        thread_current             = (thread_current_t)get("il2cpp_thread_current");
        il2cpp_free                = (free_t)get("il2cpp_free");

        g_exportsResolved = domain_get && assembly_get_image && class_from_name
                         && class_get_method_from_name && thread_attach;
        return g_exportsResolved;
    }

    // Print the table ONCE, and only when asked - the caller does this after the
    // runtime is up, so it reflects reality instead of the state at inject time.
    inline void ReportOnce() {
        if (g_reported) return;
        g_reported = true;

        auto mark = [](void* p) { return p ? "ok" : "MISSING"; };
        std::cout << TAG_INIT() << "il2cpp metadata API:\n"
            << "    domain_get              " << mark((void*)domain_get) << "\n"
            << "    domain_assembly_open    " << mark((void*)domain_assembly_open) << "\n"
            << "    domain_get_assemblies   " << mark((void*)domain_get_assemblies) << "\n"
            << "    assembly_get_image      " << mark((void*)assembly_get_image) << "\n"
            << "    class_from_name         " << mark((void*)class_from_name) << "\n"
            << "    class_get_method        " << mark((void*)class_get_method_from_name) << "\n"
            << "    class_get_parent        " << mark((void*)class_get_parent) << "\n"
            << "    class_get_name          " << mark((void*)class_get_name) << "\n"
            << "    class_get_fields        " << mark((void*)class_get_fields) << "\n"
            << "    field_get_name          " << mark((void*)field_get_name) << "\n"
            << "    field_get_offset        " << mark((void*)field_get_offset) << "\n"
            << "    thread_attach           " << mark((void*)thread_attach) << "\n"
            << "    thread_current          " << mark((void*)thread_current) << "\n";
    }

    // Kept for source compatibility with existing callers.
    inline bool Init() { return ResolveExports(); }

    // ---- readiness -------------------------------------------------------------

    /// <summary>
    /// True only when the runtime is actually up: exports resolved, a domain exists,
    /// and it reports at least one assembly. "GameAssembly.dll is loaded" is NOT the
    /// same thing and must never be used as the gate.
    /// </summary>
    inline bool RuntimeReady() {
        if (!ResolveExports()) {
            g_attachError = "il2cpp exports did not resolve";
            return false;
        }

        void* domain = SehDomainGet();
        if (!domain) {
            g_attachError = "il2cpp_domain_get returned null - runtime not initialised yet";
            return false;
        }

        // Attach FIRST, then ask questions.
        //
        // il2cpp_domain_get_assemblies and the class lookups are runtime calls. Making
        // them from a thread the runtime has never seen - repeatedly, in a poll loop -
        // is not safe, and doing it during startup is a good way to hang or destabilise
        // the game. Once this thread is attached the runtime knows about it and the
        // rest of the API is usable.
        if (SehThreadCurrent() == nullptr && SehThreadAttach(domain) == nullptr) {
            g_attachError = "il2cpp_thread_attach returned null - runtime not ready";
            return false;
        }

        // Domain alone can exist before assemblies are registered, so require a
        // non-empty assembly list. Safe now that we are attached.
        if (domain_get_assemblies) {
            size_t count = 0;
            void** list = SehGetAssemblies(domain, &count);
            if (!list || count == 0) {
                g_attachError = "runtime has no assemblies loaded yet";
                return false;
            }
        }

        g_attachError = "";
        return true;
    }

    /// <summary>
    /// Waits for the runtime, cheaply.
    ///
    /// The poll only calls domain_get() - a plain accessor - and gives up on the
    /// expensive checks until that returns something. The first version polled
    /// domain_get_assemblies every 250ms from an unattached thread for two minutes,
    /// which is a lot of pressure on a runtime that is still starting up.
    /// </summary>
    inline bool WaitForRuntime(int timeoutMs, int stepMs = 1000) {
        for (int waited = 0; waited < timeoutMs; waited += stepMs) {
            if (ResolveExports() && SehDomainGet() != nullptr) {
                // Domain exists - now do the full (attaching) check.
                if (RuntimeReady()) return true;
            }
            Sleep(stepMs);
        }
        return false;
    }

    // ---- class + method lookup -------------------------------------------------

    inline void* FindClass(const char* assembly, const char* ns, const char* name) {
        if (!RuntimeReady()) return nullptr;
        void* domain = SehDomainGet();
        void* asm_ = SehAssemblyOpen(domain, assembly);
        return SehClassFromName(SehAssemblyImage(asm_), ns, name);
    }

    inline void* FindClassAny(const char* const* assemblies, int count,
                              const char* ns, const char* name) {
        for (int i = 0; i < count; i++)
            if (void* k = FindClass(assemblies[i], ns, name)) return k;
        return nullptr;
    }

    // Search EVERY loaded assembly for a type.
    //
    // Naming the assembly is a guess - the runtime dump of this build lists 328
    // assemblies and not one UnityEngine module, so "UnityEngine.CoreModule" was never
    // more than convention. Asking the domain removes the guess.
    //
    // Only ever call this once the runtime is ready; sweeping the assembly list before
    // il2cpp_init has finished is what crashed the game.
    inline void* FindClassAnywhere(const char* ns, const char* name) {
        if (!RuntimeReady() || !domain_get_assemblies) return nullptr;

        void* domain = SehDomainGet();
        size_t count = 0;
        void** assemblies = SehGetAssemblies(domain, &count);
        if (!assemblies || count == 0 || count > 4096) return nullptr;

        for (size_t i = 0; i < count; i++) {
            void* image = SehAssemblyImage(assemblies[i]);
            if (!image) continue;
            if (void* k = SehClassFromName(image, ns, name)) return k;
        }
        return nullptr;
    }

    inline const char* kUnityAssemblies[] = {
        "UnityEngine.CoreModule",
        "UnityEngine",
        "UnityEngine.UI",
    };
    inline constexpr int kUnityAssemblyCount = 3;

    // MethodInfo's FIRST field is the native code pointer, which is what makes a
    // metadata lookup a drop-in replacement for a baked RVA.
    inline void* MethodPointer(void* methodInfo) {
        if (!methodInfo) return nullptr;
        __try { return *reinterpret_cast<void**>(methodInfo); }
        __except (EXCEPTION_EXECUTE_HANDLER) { return nullptr; }
    }

    inline void* FindMethod(void* klass, const char* name, int argc) {
        if (!klass) return nullptr;
        for (int depth = 0; klass && depth < 8; ++depth) {
            if (void* m = SehGetMethod(klass, name, argc)) return m;
            klass = SehGetParent(klass);
        }
        return nullptr;
    }

    inline void* FindUnityMethod(const char* ns, const char* type, const char* method, int argc) {
        void* klass = FindClassAny(kUnityAssemblies, kUnityAssemblyCount, ns, type);
        if (!klass) klass = FindClassAnywhere(ns, type);
        return MethodPointer(FindMethod(klass, method, argc));
    }

    // ---- threading -------------------------------------------------------------

    // Managed code must only be entered from a thread the runtime knows about. The
    // inspector renders from the D3D Present hook, which IL2CPP never attached.
    inline bool AttachCurrentThread() {
        if (!RuntimeReady()) return false;          // sets g_attachError
        if (SehThreadCurrent()) { g_attachError = ""; return true; }

        void* domain = SehDomainGet();
        if (!SehThreadAttach(domain)) {
            g_attachError = "il2cpp_thread_attach returned null";
            return false;
        }
        g_attachError = "";
        return true;
    }
}
