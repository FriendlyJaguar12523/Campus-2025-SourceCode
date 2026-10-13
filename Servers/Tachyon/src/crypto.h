#pragma once
//
// RSA private key loaded from .NET RSAKeyValue XML (RSA.ToXmlString), used to decrypt the client's
// wire blobs (AT/VB/CKA/CIA/CPK) which are RSA-encrypted to the matching public key. Wraps OpenSSL;
// no OpenSSL types leak into the header.
//
#include <cstdint>
#include <optional>
#include <string>
#include <vector>

// Standard base64 decode (ignores whitespace). Empty result on malformed input.
std::vector<uint8_t> base64_decode(const std::string& in);

class rsa_key {
public:
    rsa_key() = default;
    ~rsa_key();
    rsa_key(const rsa_key&) = delete;
    rsa_key& operator=(const rsa_key&) = delete;

    // Build from .NET RSAKeyValue XML. Returns false and sets `err` on failure.
    bool load_xml(const std::string& xml, std::string& err);
    bool valid() const { return m_pkey != nullptr; }
    int bits() const;

    // RSA-decrypt one ciphertext block, trying PKCS#1 v1.5 then OAEP(SHA-1) (the two .NET defaults).
    // Returns nullopt if neither padding yields a valid plaintext.
    std::optional<std::vector<uint8_t>> decrypt(const uint8_t* data, size_t len) const;

private:
    void* m_pkey = nullptr;   // EVP_PKEY*
};
