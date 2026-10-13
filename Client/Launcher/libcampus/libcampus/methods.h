#pragma once

#if defined(__ANDROID__) || defined(PLATFORM_QUEST)

#include <cstdint>

namespace RR::Methods::Quest {

    namespace Oculus {
        static const uintptr_t GET_APPID_VA = 0xffffff89ba0e9000ULL;
    }

    namespace TLS {
        static const uintptr_t NotifyServerCertificate = 0x0;
        static const uintptr_t LegacyTlsAuthentication = 0x0;
        static const uintptr_t GetClientCredentials = 0x0;
    }

    namespace BestHTTP {
        static const uintptr_t HTTPRequest_Send = 0x0;
        static const uintptr_t HTTPRequest_get_Uri = 0x0;
        static const uintptr_t HTTPRequest_set_Uri = 0x0;
        static const uintptr_t HTTPRequest_OnResponse = 0x0;
        static const uintptr_t HTTPRequest_ProcessResponse = 0x0;
    }

}

#endif