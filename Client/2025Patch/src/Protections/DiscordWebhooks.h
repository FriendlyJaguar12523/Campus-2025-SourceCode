#pragma once

#include <string>

namespace DiscordWebhooks {

    static const std::string ANTICHEAT_HOOK = "https://discord.com/api/webhooks/0/REDACTED";
    static const std::string CRASH_HOOK = "https://discord.com/api/webhooks/0/REDACTED";

    enum class WebhookType {
        AntiCheat,
        Crash
    };

    inline const std::string& GetWebhook(WebhookType type) {
        switch (type) {
        case WebhookType::AntiCheat:
            return ANTICHEAT_HOOK;
        case WebhookType::Crash:
            return CRASH_HOOK;
        default:
            return ANTICHEAT_HOOK;
        }
    }

    inline const std::string& GetAntiCheatWebhook() {
        return ANTICHEAT_HOOK;
    }

    inline const std::string& GetCrashWebhook() {
        return CRASH_HOOK;
    }

}