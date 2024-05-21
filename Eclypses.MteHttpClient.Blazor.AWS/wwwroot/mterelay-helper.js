﻿import {
    MteWasm,
    MteBase,
    MteStatus,
    MteMkeEnc,
    MteMkeDec,
    MteKyber,
    MteKyberStatus,
    MteKyberStrength,
} from "./Mte.js";

/**
 * Declare some variables that are global to this module.
 */
let mteWasm = null;
let mteBase = null;

// ************************************** START stuff for MTE

/**
 * An asynchronous function that instantiates MteWasm, then sets up MteBase for future use.
 * This MUST be called before any other MTE methods can be used, usually as soon as the website loads.
 */
export async function instantiateMteWasm() {
    // assign mteWasm variable, and instantiate wasm
    if (!mteWasm) {
        mteWasm = new MteWasm();
        await mteWasm.instantiate();
    }
    // assign mteBase variable
    if (!mteBase) {
        mteBase = new MteBase(mteWasm);
    }
    return true;
}

export function getVersion() {
    return mteBase.getVersion();
}

export function initLicense(licensedCompany, licenseKey) {
    // The AWS version has a static license key that only
    // works with the container hosted on AWS, so use that
    // rather than what is supplied in the settings file.
    licensedCompany = "Eclypses Inc";
    licenseKey = "9eHOohOm/GwY01xbvNTL9B+1";
    if (!mteBase.initLicense(licensedCompany, licenseKey)) {
        const licenseStatus = MteStatus.mte_status_license_error;
        const status = mteBase.getStatusName(licenseStatus);
        const message = mteBase.getStatusDescription(licenseStatus);
        throw new Error(`Error with MTE License.\n${status}: ${message}`);
    }
}

export function makeAnEmptyEncoder() {
    return MteMkeEnc.fromdefault(mteWasm);
}

export function makeAnEmptyDecoder() {
    return MteMkeDec.fromdefault(mteWasm, 1000, -63);
}

export function initializeEncoder(
    mteEncoder,
    encoderEntropy,
    nonce,
    personalization
) {
    mteEncoder.setEntropyArr(encoderEntropy);
    mteEncoder.setNonce(nonce);
    const encoderStatus = mteEncoder.instantiate(personalization);
    return encoderStatus;
}

export function initializeDecoder(
    mteDecoder,
    decoderEntropy,
    nonce,
    personalization
) {
    mteDecoder.setEntropyArr(decoderEntropy);
    mteDecoder.setNonce(nonce);
    const decoderStatus = mteDecoder.instantiate(personalization);
    return decoderStatus;
}

export function retrieveEncoderState(mteEncoder) {
    return mteEncoder.saveStateB64();
}

export function retrieveDecoderState(mteDecoder) {
    return mteDecoder.saveStateB64();
}

export function restoreDecoderState(mteDecoder, mteState) {
    return mteDecoder.restoreStateB64(mteState);
}

export function restoreEncoderState(mteEncoder, mteState) {
    return mteEncoder.restoreStateB64(mteState);
}

export function decodeToString(mteDecoder, payload) {
    var ret = mteDecoder.decodeStrB64(payload);
    if (ret.status != MteStatus.mte_status_success) {
        const status = mteBase.getStatusName(ret.status);
        const message = mteBase.getStatusDescription(ret.status);
        throw new Error(`Error decoding string.\n${status}: ${message}`);
    }
    return ret.str;
}

export function decodeToByteArray(mteDecoder, payload) {
    var ret = mteDecoder.decode(payload);
    if (ret.status != MteStatus.mte_status_success) {
        const status = mteBase.getStatusName(ret.status);
        const message = mteBase.getStatusDescription(ret.status);
        throw new Error(`Error decoding byte array.\n${status}: ${message}`);
    }
    return ret.arr;
}

export function encodeToString(mteEncoder, payload) {
    var ret = mteEncoder.encodeStrB64(payload);
    if (ret.status != MteStatus.mte_status_success) {
        const status = mteBase.getStatusName(ret.status);
        const message = mteBase.getStatusDescription(ret.status);
        throw new Error(`Error encoding string.\n${status}: ${message}`);
    }
    return ret;
}

export function encode(mteEncoder, payload) {
    var ret = mteEncoder.encode(payload);
    if (ret.status != MteStatus.mte_status_success) {
        const status = mteBase.getStatusName(ret.status);
        const message = mteBase.getStatusDescription(ret.status);
        throw new Error(`Error encoding payload.\n${status}: ${message}`);
    }
    var ret2 = { status: ret.status, str: uint8ToBase64(ret.arr) };

    return ret2;
}

// ************************************** END stuff for MTE

// *************************************** START stuff for Kyber
let kyberPairsD = new Object();
let kyberPairsE = new Object();

export async function makeKyberKeyPairs(pairId) {
      if (!mteWasm) {
        throw new Error("mteWasm has not been created yet.");
    }
    if (!kyberPairsD[pairId]) {
        var d = new MteKyber(mteWasm, MteKyberStrength.K512);
        kyberPairsD[pairId] = d;
    }
    if (!kyberPairsE[pairId]) {
        var e = new MteKyber(mteWasm, MteKyberStrength.K512);
        kyberPairsE[pairId] = e;
    }
    var keyPairE = kyberPairsE[pairId].createKeypair();
    var keyPairD = kyberPairsD[pairId].createKeypair();
    if (keyPairE.status !== MteKyberStatus.success) {
        throw new Error("Failed to create Kyber key pair.");
    }    
    if (keyPairD.status !== MteKyberStatus.success) {
        throw new Error("Failed to create Kyber key pair.");
    }
    var publicKeyE = uint8ToBase64(keyPairE.result1);
    var publicKeyD = uint8ToBase64(keyPairD.result1);
      return { EncoderPublicKey: publicKeyE, DecoderPublicKey: publicKeyD, Erc: keyPairE.status, Drc: keyPairD.status };
}

export async function makeKyberSecrets(pairId, encoderEncryptedB64, decoderEncryptedB64) {
    if (!kyberPairsE[pairId]) {
        throw new Error("mteKyberE has not been created yet.");
    }
    if (!kyberPairsD[pairId]) {
        throw new Error("mteKyberD has not been created yet.");
    }
    var eSecretBytes = base64ToUint8(encoderEncryptedB64);    
    var dSecretBytes = base64ToUint8(decoderEncryptedB64);
    var resultE = kyberPairsE[pairId].decryptSecret(eSecretBytes);
    var resultD = kyberPairsD[pairId].decryptSecret(dSecretBytes);
    delete kyberPairsE[pairId];
    delete kyberPairsD[pairId];
    return { EncoderSecret: uint8ToBase64(resultE.result1), DecoderSecret: uint8ToBase64(resultD.result1), Erc: resultE.status, Drc: resultD.status }
}

// *************************************** END stuff for Kyber



// *************************************** START support methods

/**
 * Utility to convert an array of bytes to a base64 string.
 * @param buffer An array of bytes
 * @returns A base64 encoded string of the original array.
 */
function uint8ToBase64(buffer) {
    return btoa(String.fromCharCode.apply(null, new Uint8Array(buffer)));
}

/**
 * Utility to convert a base64 string into an array of bytes.
 * @param base64Str A base64 encoded string.
 * @returns An array of bytes.
 */
function base64ToUint8(base64Str) {
    const arr = window.atob(base64Str);
    let i = 0;
    const iMax = arr.length;
    const bytes = new Uint8Array(iMax);
    for (; i < iMax; ++i) {
        bytes[i] = arr.charCodeAt(i);
    }
    return bytes;
}
// *************************************** END support methods