// DiscForge — proprietary. Copyright (c) 2026 MaTRIX TeAm. All rights reserved.
// Not open source. No permission is granted to copy, fork or redistribute.
//
// .NET 8's browser-wasm runtime has no ECDSA implementation (System.Security.Cryptography.ECDsa.Create()
// throws PlatformNotSupportedException there) — see docs/DIFFERENTIATORS.md and the v1.105.0/v1.106.0
// CHANGELOG entries for how this was found. This module verifies the identical signature through the
// browser's own Web Crypto API (SubtleCrypto) instead, so the page can still check a submitter's or a
// merge certificate's ECDSA (P-256/SHA-256) signature even though .NET's own ECDsa can't run here.
//
// Web Crypto conventions this relies on, both confirmed against DiscForge.Core's actual output:
//  - importKey('spki', ...) accepts the exact SubjectPublicKeyInfo bytes ECDsa.ExportSubjectPublicKeyInfo()
//    produces — no reformatting needed.
//  - crypto.subtle.verify with {name:'ECDSA', hash:'SHA-256'} expects the signature as raw r||s
//    (the "IEEE P1363" concatenated form), which is .NET's ECDsa.SignData default (DSASignatureFormat.
//    IeeeP1363FixedFieldConcatenation) — again, no reformatting needed.
// Both were verified end-to-end against real DiscForge.Core-signed data via a headless-browser test
// before this was trusted, not assumed from documentation alone.

/**
 * Verify an ECDSA P-256 / SHA-256 signature using the browser's Web Crypto API.
 * @param {Uint8Array} spki - the public key, as SubjectPublicKeyInfo bytes.
 * @param {Uint8Array} data - the exact bytes that were signed.
 * @param {Uint8Array} signature - the signature, as raw r||s (IEEE P1363) bytes.
 * @returns {Promise<boolean>} true if the signature verifies, false if it does not.
 * @throws if this browser has no usable SubtleCrypto (e.g. an insecure, non-localhost context).
 */
export async function verifyEcdsaP256Sha256(spki, data, signature) {
    if (!globalThis.crypto || !globalThis.crypto.subtle) {
        throw new Error('This browser has no window.crypto.subtle available (requires a secure context: https, or localhost).');
    }
    const key = await crypto.subtle.importKey(
        'spki',
        spki,
        { name: 'ECDSA', namedCurve: 'P-256' },
        false,
        ['verify'],
    );
    return await crypto.subtle.verify(
        { name: 'ECDSA', hash: 'SHA-256' },
        key,
        signature,
        data,
    );
}
