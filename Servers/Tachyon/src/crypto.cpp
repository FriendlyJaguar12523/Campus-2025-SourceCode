#include "crypto.h"

#include <openssl/evp.h>
#include <openssl/rsa.h>
#include <openssl/core_names.h>
#include <openssl/param_build.h>
#include <openssl/bn.h>

#include <array>

// ------------------------------------------------------------------ base64

std::vector<uint8_t> base64_decode(const std::string& in) {
    static std::array<int8_t, 256> table = [] {
        std::array<int8_t, 256> t{};
        t.fill(-1);
        const char* a = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        for (int i = 0; i < 64; i++) t[(uint8_t) a[i]] = (int8_t) i;
        return t;
    }();

    std::vector<uint8_t> out;
    int val = 0, bits = 0;
    for (char c : in) {
        if (c == '=' || c == '\n' || c == '\r' || c == ' ' || c == '\t') continue;
        int8_t d = table[(uint8_t) c];
        if (d < 0) return {};   // malformed
        val = (val << 6) | d;
        bits += 6;
        if (bits >= 8) {
            bits -= 8;
            out.push_back((uint8_t) ((val >> bits) & 0xFF));
        }
    }
    return out;
}

// ------------------------------------------------------------------ RSA key

namespace {
    // Extract the base64 body of <Tag>...</Tag> from the XML, then decode it to a BIGNUM.
    BIGNUM* xml_bn(const std::string& xml, const char* tag) {
        std::string open = std::string("<") + tag + ">";
        std::string close = std::string("</") + tag + ">";
        auto a = xml.find(open);
        if (a == std::string::npos) return nullptr;
        a += open.size();
        auto b = xml.find(close, a);
        if (b == std::string::npos) return nullptr;
        auto bytes = base64_decode(xml.substr(a, b - a));
        if (bytes.empty()) return nullptr;
        return BN_bin2bn(bytes.data(), (int) bytes.size(), nullptr);
    }
}

rsa_key::~rsa_key() {
    if (m_pkey) EVP_PKEY_free((EVP_PKEY*) m_pkey);
}

bool rsa_key::load_xml(const std::string& xml, std::string& err) {
    // n, e, d are required; the CRT factors (p/q/dp/dq/iq) are optional but included when present.
    BIGNUM* n = xml_bn(xml, "Modulus");
    BIGNUM* e = xml_bn(xml, "Exponent");
    BIGNUM* d = xml_bn(xml, "D");
    BIGNUM* p = xml_bn(xml, "P");
    BIGNUM* q = xml_bn(xml, "Q");
    BIGNUM* dp = xml_bn(xml, "DP");
    BIGNUM* dq = xml_bn(xml, "DQ");
    BIGNUM* iq = xml_bn(xml, "InverseQ");

    auto cleanup = [&] {
        for (BIGNUM* b : {n, e, d, p, q, dp, dq, iq}) BN_free(b);
    };

    if (!n || !e || !d) { err = "RSAKeyValue XML missing Modulus/Exponent/D"; cleanup(); return false; }

    OSSL_PARAM_BLD* bld = OSSL_PARAM_BLD_new();
    OSSL_PARAM_BLD_push_BN(bld, OSSL_PKEY_PARAM_RSA_N, n);
    OSSL_PARAM_BLD_push_BN(bld, OSSL_PKEY_PARAM_RSA_E, e);
    OSSL_PARAM_BLD_push_BN(bld, OSSL_PKEY_PARAM_RSA_D, d);
    if (p && q) {
        OSSL_PARAM_BLD_push_BN(bld, OSSL_PKEY_PARAM_RSA_FACTOR1, p);
        OSSL_PARAM_BLD_push_BN(bld, OSSL_PKEY_PARAM_RSA_FACTOR2, q);
        if (dp) OSSL_PARAM_BLD_push_BN(bld, OSSL_PKEY_PARAM_RSA_EXPONENT1, dp);
        if (dq) OSSL_PARAM_BLD_push_BN(bld, OSSL_PKEY_PARAM_RSA_EXPONENT2, dq);
        if (iq) OSSL_PARAM_BLD_push_BN(bld, OSSL_PKEY_PARAM_RSA_COEFFICIENT1, iq);
    }
    OSSL_PARAM* params = OSSL_PARAM_BLD_to_param(bld);

    EVP_PKEY_CTX* ctx = EVP_PKEY_CTX_new_from_name(nullptr, "RSA", nullptr);
    EVP_PKEY* pkey = nullptr;
    bool ok = ctx && params &&
              EVP_PKEY_fromdata_init(ctx) > 0 &&
              EVP_PKEY_fromdata(ctx, &pkey, EVP_PKEY_KEYPAIR, params) > 0;

    OSSL_PARAM_free(params);
    OSSL_PARAM_BLD_free(bld);
    if (ctx) EVP_PKEY_CTX_free(ctx);
    cleanup();

    if (!ok || !pkey) { err = "failed to build RSA key from XML parameters"; return false; }
    m_pkey = pkey;
    return true;
}

int rsa_key::bits() const {
    return m_pkey ? EVP_PKEY_get_bits((EVP_PKEY*) m_pkey) : 0;
}

std::optional<std::vector<uint8_t>> rsa_key::decrypt(const uint8_t* data, size_t len) const {
    if (!m_pkey) return std::nullopt;

    for (int padding : {RSA_PKCS1_PADDING, RSA_PKCS1_OAEP_PADDING}) {
        EVP_PKEY_CTX* ctx = EVP_PKEY_CTX_new((EVP_PKEY*) m_pkey, nullptr);
        if (!ctx) continue;
        std::optional<std::vector<uint8_t>> result;
        if (EVP_PKEY_decrypt_init(ctx) > 0 &&
            EVP_PKEY_CTX_set_rsa_padding(ctx, padding) > 0) {
            size_t out_len = 0;
            if (EVP_PKEY_decrypt(ctx, nullptr, &out_len, data, len) > 0) {
                std::vector<uint8_t> out(out_len);
                if (EVP_PKEY_decrypt(ctx, out.data(), &out_len, data, len) > 0) {
                    out.resize(out_len);
                    result = std::move(out);
                }
            }
        }
        EVP_PKEY_CTX_free(ctx);
        if (result) return result;
    }
    return std::nullopt;
}
