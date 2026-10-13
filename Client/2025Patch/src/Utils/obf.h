#pragma once
// Compile-time string obfuscation (XOR). A string wrapped in skCrypt("...") is stored ENCRYPTED
// in the binary's .rdata and only decrypted into a stack buffer at runtime, so `strings` / a hex
// editor never sees the plaintext. Use it for anything that must not be readable in the shipped
// DLL - above all the patch signature. This is the well-known skCrypter (skadro) construction.
//
// Usage:  someFn(skCrypt("secret"));            // -> const char*
//         someWideFn(skCrypt(L"secret"));       // -> const wchar_t*
// The decrypted buffer lives in a temporary valid for the full expression, so pass it straight
// into a call that copies the string (WinHttpConnect, il2cpp_string_new, std::wstring ctor, ...).

#include <type_traits>

#ifndef SKCRYPTER
#define SKCRYPTER

namespace skc {
    template <class _Ty>
    using clean_type = typename std::remove_const_t<std::remove_reference_t<_Ty>>;

    template <int _size, char _key1, char _key2, typename T>
    class skCrypter
    {
    public:
        __forceinline constexpr skCrypter(T* data)
        {
            crypt(data);
        }

        __forceinline T* get() { return _storage; }
        __forceinline int  size() { return _size; }
        __forceinline char key() { return _key1; }

        __forceinline T* encrypt(T* data)
        {
            if (!isEncrypted()) crypt(data);
            return _storage;
        }

        __forceinline T* decrypt()
        {
            if (isEncrypted()) crypt(_storage);
            return _storage;
        }

        __forceinline bool isEncrypted() { return _storage[_size - 1] != 0; }

        __forceinline void clear()
        {
            for (int i = 0; i < _size; i++) _storage[i] = 0;
        }

        __forceinline operator T* ()
        {
            decrypt();
            return _storage;
        }

    private:
        __forceinline constexpr void crypt(T* data)
        {
            for (int i = 0; i < _size; i++)
                _storage[i] = data[i] ^ (_key1 + i % (1 + _key2));
        }

        T _storage[_size]{};
    };
}

// __TIME__ digits give a per-build key; each call site gets its own encrypted copy.
#define skCrypt(str) skCrypt_key(str, __TIME__[4], __TIME__[7])
#define skCrypt_key(str, key1, key2) []() { \
            constexpr static auto crypted = skc::skCrypter \
                <sizeof(str) / sizeof(str[0]), key1, key2, skc::clean_type<decltype(str[0])>>((skc::clean_type<decltype(str[0])>*)str); \
                    return crypted; }()

#endif
