﻿/*
 *  Pkcs7SignatureGenerator
 *  Example application for PKCS#7 signature creation with Pkcs11Interop and BouncyCastle libraries
 *  Copyright (c) 2014 JWC s.r.o. <http://www.jwc.sk>
 *  Author: Jaroslav Imrich <jimrich@jimrich.sk>
 *
 *  Licensing for open source projects:
 *  Pkcs7SignatureGenerator is available under the terms of the GNU Affero General 
 *  Public License version 3 as published by the Free Software Foundation.
 *  Please see <http://www.gnu.org/licenses/agpl-3.0.html> for more details.
 *
 *  Licensing for other types of projects:
 *  Pkcs7SignatureGenerator is available under the terms of flexible commercial license.
 *  Please contact JWC s.r.o. at <info@pkcs11interop.net> for more details.
 */

using System;
using System.Collections.Generic;
using Net.Pkcs11Interop.Common;
using Net.Pkcs11Interop.HighLevelAPI;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Cms;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using BCX509 = Org.BouncyCastle.X509;

namespace Pkcs7SignatureGenerator
{
    /// <summary>
    /// PKCS#7 signature creator that uses RSA private key stored on PKCS#11 compatible device.
    /// In multithreaded environment one instance of this class should be reused by all the threads.
    /// </summary>
    public class Pkcs7SignatureGenerator : IDisposable
    {
        #region Variables

        /// <summary>
        /// Flag indicating whether instance has been disposed
        /// </summary>
        private bool _disposed = false;

        /// <summary>
        /// High level PKCS#11 wrapper
        /// </summary>
        private Pkcs11 _pkcs11 = null;

        /// <summary>
        /// Logical reader with token used for signing
        /// </summary>
        private Slot _slot = null;

        /// <summary>
        /// Master session where user is logged in
        /// </summary>
        private Session _session = null;

        /// <summary>
        /// Handle of private key used for signing 
        /// </summary>
        private ObjectHandle _privateKeyHandle = null;

        /// <summary>
        /// Label (value of CKA_LABEL attribute) of the private key used for signing
        /// </summary>
        private string _ckaLabel = null;

        /// <summary>
        /// Identifier (value of CKA_ID attribute) of the private key used for signing
        /// </summary>
        private byte[] _ckaId = null;

        /// <summary>
        /// Hash algorihtm used for the signature creation
        /// </summary>
        private HashAlgorithm _hashAlgorihtm = HashAlgorithm.SHA512;

        /// <summary>
        /// Raw data of certificate related to private key used for signing
        /// </summary>
        private byte[] _signingCertificate = null;

        /// <summary>
        /// Raw data of all certificates stored in device
        /// </summary>
        private List<byte[]> _allCertificates = null;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the Pkcs7Signature class
        /// </summary>
        /// <param name="libraryPath">Path to the unmanaged PCKS#11 library</param>
        /// <param name="tokenSerial">Serial number of the token (smartcard) that contains signing key. May be null if tokenLabel is specified.</param>
        /// <param name="tokenLabel">Label of of the token (smartcard) that contains signing key. May be null if tokenSerial is specified.</param>
        /// <param name="pin">PIN for the token (smartcard)</param>
        /// <param name="ckaLabel">Label (value of CKA_LABEL attribute) of the private key used for signing. May be null if ckaId is specified.</param>
        /// <param name="ckaId">Hex encoded string with identifier (value of CKA_ID attribute) of the private key used for signing. May be null if ckaLabel is specified.</param>
        /// <param name="hashAlgorihtm">Hash algorihtm used for the signature creation</param>
        public Pkcs7SignatureGenerator(string libraryPath, string tokenSerial, string tokenLabel, string pin, string ckaLabel, string ckaId, HashAlgorithm hashAlgorihtm)
        {
            byte[] pinValue = (pin == null) ? null : ConvertUtils.Utf8StringToBytes(pin);
            byte[] ckaIdValue = (ckaId == null) ? null : ConvertUtils.HexStringToBytes(ckaId);
            InitializePkcs7RsaSignature(libraryPath, tokenSerial, tokenLabel, pinValue, ckaLabel, ckaIdValue, hashAlgorihtm);
        }

        /// <summary>
        /// Initializes a new instance of the Pkcs11Signature class
        /// </summary>
        /// <param name="libraryPath">Path to the unmanaged PCKS#11 library</param>
        /// <param name="tokenSerial">Serial number of the token (smartcard) that contains signing key. May be null if tokenLabel is specified.</param>
        /// <param name="tokenLabel">Label of of the token (smartcard) that contains signing key. May be null if tokenSerial is specified.</param>
        /// <param name="pin">PIN for the token (smartcard)</param>
        /// <param name="ckaLabel">Label (value of CKA_LABEL attribute) of the private key used for signing. May be null if ckaId is specified.</param>
        /// <param name="ckaId">Identifier (value of CKA_ID attribute) of the private key used for signing. May be null if ckaLabel is specified.</param>
        /// <param name="hashAlgorihtm">Hash algorihtm used for the signature creation</param>
        public Pkcs7SignatureGenerator(string libraryPath, string tokenSerial, string tokenLabel, byte[] pin, string ckaLabel, byte[] ckaId, HashAlgorithm hashAlgorihtm)
        {
            InitializePkcs7RsaSignature(libraryPath, tokenSerial, tokenLabel, pin, ckaLabel, ckaId, hashAlgorihtm);
        }

        /// <summary>
        /// Initializes a new instance of the Pkcs11Signature class
        /// </summary>
        /// <param name="libraryPath">Path to the unmanaged PCKS#11 library</param>
        /// <param name="tokenSerial">Serial number of the token (smartcard) that contains signing key. May be null if tokenLabel is specified.</param>
        /// <param name="tokenLabel">Label of of the token (smartcard) that contains signing key. May be null if tokenSerial is specified.</param>
        /// <param name="pin">PIN for the token (smartcard)</param>
        /// <param name="ckaLabel">Label (value of CKA_LABEL attribute) of the private key used for signing. May be null if ckaId is specified.</param>
        /// <param name="ckaId">Identifier (value of CKA_ID attribute) of the private key used for signing. May be null if ckaLabel is specified.</param>
        /// <param name="hashAlgorihtm">Hash algorihtm used for the signature creation</param>
        private void InitializePkcs7RsaSignature(string libraryPath, string tokenSerial, string tokenLabel, byte[] pin, string ckaLabel, byte[] ckaId, HashAlgorithm hashAlgorihtm)
        {
            try
            {
                if (string.IsNullOrEmpty(libraryPath))
                    throw new ArgumentNullException("libraryPath");

                _pkcs11 = new Pkcs11(libraryPath, true);

                _slot = FindSlot(tokenSerial, tokenLabel);
                if (_slot == null)
                    throw new TokenNotFoundException(string.Format("Token with serial \"{0}\" and label \"{1}\" was not found", tokenSerial, tokenLabel));

                _session = _slot.OpenSession(true);
                _session.Login(CKU.CKU_USER, pin);

                _privateKeyHandle = FindPrivateKey(ckaLabel, ckaId);

                _ckaLabel = ckaLabel;
                _ckaId = ckaId;

                if (!Enum.IsDefined(typeof(HashAlgorithm), hashAlgorihtm))
                    throw new ArgumentException("Invalid hash algorithm specified");

                _hashAlgorihtm = hashAlgorihtm;
            }
            catch
            {
                if (_session != null)
                {
                    _session.Dispose();
                    _session = null;
                }

                if (_pkcs11 != null)
                {
                    _pkcs11.Dispose();
                    _pkcs11 = null;
                }

                throw;
            }
        }

        #endregion

        #region Certificates

        /// <summary>
        /// Gets the raw data of certificate related to private key used for signing
        /// </summary>
        /// <returns>Raw data of certificate related to private key used for signing</returns>
        public byte[] GetSigningCertificate()
        {
            if (this._disposed)
                throw new ObjectDisposedException(this.GetType().FullName);

            // Don't read certificate from token if it has already been read
            if (_signingCertificate == null)
            {
                using (Session session = _slot.OpenSession(true))
                {
                    List<ObjectAttribute> searchTemplate = new List<ObjectAttribute>();
                    searchTemplate.Add(new ObjectAttribute(CKA.CKA_CLASS, CKO.CKO_CERTIFICATE));
                    searchTemplate.Add(new ObjectAttribute(CKA.CKA_CERTIFICATE_TYPE, CKC.CKC_X_509));
                    if (!string.IsNullOrEmpty(_ckaLabel))
                        searchTemplate.Add(new ObjectAttribute(CKA.CKA_LABEL, _ckaLabel));
                    if (_ckaId != null)
                        searchTemplate.Add(new ObjectAttribute(CKA.CKA_ID, _ckaId));

                    List<ObjectHandle> foundObjects = _session.FindAllObjects(searchTemplate);
                    if (foundObjects.Count < 1)
                        throw new ObjectNotFoundException(string.Format("Certificate with label \"{0}\" and id \"{1}\" was not found", _ckaLabel, ConvertUtils.BytesToHexString(_ckaId)));
                    else if (foundObjects.Count > 1)
                        throw new ObjectNotFoundException(string.Format("More than one certificate with label \"{0}\" and id \"{1}\" was found", _ckaLabel, ConvertUtils.BytesToHexString(_ckaId)));

                    List<CKA> attributes = new List<CKA>();
                    attributes.Add(CKA.CKA_VALUE);

                    List<ObjectAttribute> certificateAttributes = _session.GetAttributeValue(foundObjects[0], attributes);
                    _signingCertificate = certificateAttributes[0].GetValueAsByteArray();
                }
            }

            return _signingCertificate;
        }

        /// <summary>
        /// Gets the raw data of all certificates stored in device
        /// </summary>
        /// <returns>Raw data of all certificates stored in device</returns>
        public List<byte[]> GetAllCertificates()
        {
            if (this._disposed)
                throw new ObjectDisposedException(this.GetType().FullName);

            // Don't read certificates from token if they have already been read
            if (_allCertificates == null)
            {
                List<byte[]> certificates = new List<byte[]>();

                using (Session session = _slot.OpenSession(true))
                {
                    List<ObjectAttribute> searchTemplate = new List<ObjectAttribute>();
                    searchTemplate.Add(new ObjectAttribute(CKA.CKA_CLASS, CKO.CKO_CERTIFICATE));
                    searchTemplate.Add(new ObjectAttribute(CKA.CKA_CERTIFICATE_TYPE, CKC.CKC_X_509));

                    List<CKA> attributes = new List<CKA>();
                    attributes.Add(CKA.CKA_VALUE);

                    List<ObjectHandle> foundObjects = _session.FindAllObjects(searchTemplate);
                    foreach (ObjectHandle foundObject in foundObjects)
                    {
                        List<ObjectAttribute> objectAttributes = _session.GetAttributeValue(foundObject, attributes);
                        certificates.Add(objectAttributes[0].GetValueAsByteArray());
                    }
                }

                _allCertificates = certificates;
            }

            return _allCertificates;
        }

        #endregion

        /// <summary>
        /// Generates PKCS#7 signature of specified data
        /// </summary>
        /// <param name="data">Data to be signed</param>
        /// <param name="detached">Flag indicating whether detached signature should be produced</param>
        /// <param name="signingCertificate">Signing certificate</param>
        /// <param name="certPath">Certification path for signing certificate</param>
        /// <returns>DER encoded PKCS#7 signature of specified data</returns>
        public byte[] GenerateSignature(byte[] data, bool detached, BCX509.X509Certificate signingCertificate, ICollection<BCX509.X509Certificate> certPath)
        {
            if (this._disposed)
                throw new ObjectDisposedException(this.GetType().FullName);

            string hashOid = GetHashOid(_hashAlgorihtm);
            IDigest hashGenerator = GetHashGenerator(_hashAlgorihtm);

            // Compute hash of input data
            byte[] dataHash = ComputeDigest(hashGenerator, data);

            // Construct SignerInfo.signedAttrs
            Asn1EncodableVector signedAttributesVector = new Asn1EncodableVector();

            // Add PKCS#9 contentType signed attribute
            signedAttributesVector.Add(
                new Org.BouncyCastle.Asn1.Cms.Attribute(
                    new DerObjectIdentifier(OID.PKCS9AtContentType),
                    new DerSet(new DerObjectIdentifier(OID.PKCS7IdData))));

            // Add PKCS#9 messageDigest signed attribute
            signedAttributesVector.Add(
                new Org.BouncyCastle.Asn1.Cms.Attribute(
                    new DerObjectIdentifier(OID.PKCS9AtMessageDigest),
                    new DerSet(new DerOctetString(dataHash))));

            // Add PKCS#9 signingTime signed attribute
            signedAttributesVector.Add(
                new Org.BouncyCastle.Asn1.Cms.Attribute(
                    new DerObjectIdentifier(OID.PKCS9AtSigningTime),
                    new DerSet(new Org.BouncyCastle.Asn1.Cms.Time(new DerUtcTime(DateTime.UtcNow)))));

            DerSet signedAttributes = new DerSet(signedAttributesVector);

            // Sign SignerInfo.signedAttrs with PKCS#1 v1.5 RSA signature using private key stored on PKCS#11 compatible device
            byte[] pkcs1Digest = ComputeDigest(hashGenerator, signedAttributes.GetDerEncoded());
            byte[] pkcs1DigestInfo = CreateDigestInfo(pkcs1Digest, hashOid);
            byte[] pkcs1Signature = null;

            using (Session session = _slot.OpenSession(true))
            using (Mechanism mechanism = new Mechanism(CKM.CKM_RSA_PKCS))
                pkcs1Signature = session.Sign(mechanism, _privateKeyHandle, pkcs1DigestInfo);

            // Construct SignerInfo
            SignerInfo signerInfo = new SignerInfo(
                new SignerIdentifier(new IssuerAndSerialNumber(signingCertificate.IssuerDN, signingCertificate.SerialNumber)),
                new AlgorithmIdentifier(new DerObjectIdentifier(hashOid), null),
                signedAttributes,
                new AlgorithmIdentifier(new DerObjectIdentifier(OID.PKCS1RsaEncryption), null),
                new DerOctetString(pkcs1Signature),
                null);

            // Construct SignedData.digestAlgorithms
            Asn1EncodableVector digestAlgorithmsVector = new Asn1EncodableVector();
            digestAlgorithmsVector.Add(new AlgorithmIdentifier(new DerObjectIdentifier(hashOid), null));

            // Construct SignedData.encapContentInfo
            ContentInfo encapContentInfo = new ContentInfo(
                new DerObjectIdentifier(OID.PKCS7IdData),
                (detached) ? null : new DerOctetString(data));

            // Construct SignedData.certificates
            Asn1EncodableVector certificatesVector = new Asn1EncodableVector();
            foreach (BCX509.X509Certificate cert in certPath)
                certificatesVector.Add(X509CertificateStructure.GetInstance(Asn1Object.FromByteArray(cert.GetEncoded())));

            // Construct SignedData.signerInfos
            Asn1EncodableVector signerInfosVector = new Asn1EncodableVector();
            signerInfosVector.Add(signerInfo.ToAsn1Object());

            // Construct SignedData
            SignedData signedData = new SignedData(
                new DerSet(digestAlgorithmsVector),
                encapContentInfo,
                new BerSet(certificatesVector),
                null,
                new DerSet(signerInfosVector));

            // Construct top level ContentInfo
            ContentInfo contentInfo = new ContentInfo(
                new DerObjectIdentifier(OID.PKCS7IdSignedData),
                signedData);

            return contentInfo.GetDerEncoded();
        }

        #region Private methods

        /// <summary>
        /// Finds slot containing the token that matches specified criteria
        /// </summary>
        /// <param name="tokenSerial">Serial number of token that should be found</param>
        /// <param name="tokenLabel">Label of token that should be found</param>
        /// <returns>Slot containing the token that matches specified criteria</returns>
        private Slot FindSlot(string tokenSerial, string tokenLabel)
        {
            if (this._disposed)
                throw new ObjectDisposedException(this.GetType().FullName);

            if (string.IsNullOrEmpty(tokenSerial) && string.IsNullOrEmpty(tokenLabel))
                throw new ArgumentException("Token serial and/or label has to be specified");

            List<Slot> slots = _pkcs11.GetSlotList(true);
            foreach (Slot slot in slots)
            {
                TokenInfo tokenInfo = null;

                try
                {
                    tokenInfo = slot.GetTokenInfo();
                }
                catch (Pkcs11Exception ex)
                {
                    if (ex.RV != CKR.CKR_TOKEN_NOT_RECOGNIZED && ex.RV != CKR.CKR_TOKEN_NOT_PRESENT)
                        throw;
                }

                if (tokenInfo == null)
                    continue;

                if (!string.IsNullOrEmpty(tokenSerial))
                    if (0 != String.Compare(tokenSerial, tokenInfo.SerialNumber, StringComparison.InvariantCultureIgnoreCase))
                        continue;

                if (!string.IsNullOrEmpty(tokenLabel))
                    if (0 != String.Compare(tokenLabel, tokenInfo.Label, StringComparison.InvariantCultureIgnoreCase))
                        continue;

                return slot;
            }

            return null;
        }

        /// <summary>
        /// Finds private key that matches specified criteria
        /// </summary>
        /// <param name="ckaLabel">Label (value of CKA_LABEL attribute) of the private key</param>
        /// <param name="ckaId">Identifier (value of CKA_ID attribute) of the private key</param>
        /// <returns>Handle of private key that matches specified criteria</returns>
        private ObjectHandle FindPrivateKey(string ckaLabel, byte[] ckaId)
        {
            if (this._disposed)
                throw new ObjectDisposedException(this.GetType().FullName);

            if (string.IsNullOrEmpty(ckaLabel) && ckaId == null)
                throw new ArgumentException("Private key label and/or id has to be specified");

            using (Session session = _slot.OpenSession(true))
            {
                List<ObjectAttribute> searchTemplate = new List<ObjectAttribute>();
                searchTemplate.Add(new ObjectAttribute(CKA.CKA_CLASS, CKO.CKO_PRIVATE_KEY));
                searchTemplate.Add(new ObjectAttribute(CKA.CKA_KEY_TYPE, CKK.CKK_RSA));
                if (!string.IsNullOrEmpty(ckaLabel))
                    searchTemplate.Add(new ObjectAttribute(CKA.CKA_LABEL, ckaLabel));
                if (ckaId != null)
                    searchTemplate.Add(new ObjectAttribute(CKA.CKA_ID, ckaId));

                List<ObjectHandle> foundObjects = _session.FindAllObjects(searchTemplate);
                if (foundObjects.Count < 1)
                    throw new ObjectNotFoundException(string.Format("Private key with label \"{0}\" and id \"{1}\" was not found", ckaLabel, ConvertUtils.BytesToHexString(ckaId)));
                else if (foundObjects.Count > 1)
                    throw new ObjectNotFoundException(string.Format("More than one private key with label \"{0}\" and id \"{1}\" was found", ckaLabel, ConvertUtils.BytesToHexString(ckaId)));

                return foundObjects[0];
            }
        }

        /// <summary>
        /// Returns OID of specified hash algorithm
        /// </summary>
        /// <param name="hashAlgorithm">Hash algorithm</param>
        /// <returns>OID of specified hash algorithm</returns>
        private static string GetHashOid(HashAlgorithm hashAlgorithm)
        {
            string oid = null;

            switch (hashAlgorithm)
            {
                case HashAlgorithm.SHA1:
                    oid = OID.SHA1;
                    break;
                case HashAlgorithm.SHA256:
                    oid = OID.SHA256;
                    break;
                case HashAlgorithm.SHA384:
                    oid = OID.SHA384;
                    break;
                case HashAlgorithm.SHA512:
                    oid = OID.SHA512;
                    break;
                default:
                    throw new NotSupportedException("Unsupported hash algorithm");
            }

            return oid;
        }

        /// <summary>
        /// Returns implementation of specified hash algorithm
        /// </summary>
        /// <param name="hashAlgorithm">Hash algorithm</param>
        /// <returns>Implementation of specified hash algorithm</returns>
        private static IDigest GetHashGenerator(HashAlgorithm hashAlgorithm)
        {
            IDigest digest = null;

            switch (hashAlgorithm)
            {
                case HashAlgorithm.SHA1:
                    digest = new Sha1Digest();
                    break;
                case HashAlgorithm.SHA256:
                    digest = new Sha256Digest();
                    break;
                case HashAlgorithm.SHA384:
                    digest = new Sha384Digest();
                    break;
                case HashAlgorithm.SHA512:
                    digest = new Sha512Digest();
                    break;
                default:
                    throw new NotSupportedException("Unsupported hash algorithm");
            }

            return digest;
        }

        /// <summary>
        /// Creates PKCS#1 DigestInfo
        /// </summary>
        /// <param name="hash">Hash value</param>
        /// <param name="hashOid">Hash algorithm OID</param>
        /// <returns>DER encoded PKCS#1 DigestInfo</returns>
        private static byte[] CreateDigestInfo(byte[] hash, string hashOid)
        {
            DerObjectIdentifier derObjectIdentifier = new DerObjectIdentifier(hashOid);
            AlgorithmIdentifier algorithmIdentifier = new AlgorithmIdentifier(derObjectIdentifier, null);
            DigestInfo digestInfo = new DigestInfo(algorithmIdentifier, hash);
            return digestInfo.GetDerEncoded();
        }

        /// <summary>
        /// Computes hash of the data
        /// </summary>
        /// <param name="digest">Hash algorithm implementation</param>
        /// <param name="data">Data that should be processed</param>
        /// <returns>Hash of data</returns>
        private static byte[] ComputeDigest(IDigest digest, byte[] data)
        {
            if (digest == null)
                throw new ArgumentNullException("digest");

            if (data == null)
                throw new ArgumentNullException("data");

            byte[] hash = new byte[digest.GetDigestSize()];

            digest.Reset();
            digest.BlockUpdate(data, 0, data.Length);
            digest.DoFinal(hash, 0);

            return hash;
        }

        #endregion

        #region IDisposable

        /// <summary>
        /// Disposes object
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        
        /// <summary>
        /// Disposes object
        /// </summary>
        /// <param name="disposing">Flag indicating whether managed resources should be disposed</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!this._disposed)
            {
                // Dispose managed objects
                if (disposing)
                {
                    _allCertificates = null;
                    _signingCertificate = null;
                    _hashAlgorihtm = HashAlgorithm.SHA512;
                    _ckaId = null;
                    _ckaLabel = null;
                    _privateKeyHandle = null;

                    if (_session != null)
                    {
                        try
                        {
                            _session.Logout();
                        }
                        catch
                        {
                            // Any exceptions can be safely ignored here
                        }

                        _session.Dispose();
                        _session = null;
                    }
                    
                    _slot = null;
                    
                    if (_pkcs11 != null)
                    {
                        _pkcs11.Dispose();
                        _pkcs11 = null;
                    }
                }
                
                // Dispose unmanaged objects

                _disposed = true;
            }
        }
        
        /// <summary>
        /// Class destructor that disposes object if caller forgot to do so
        /// </summary>
        ~Pkcs7SignatureGenerator()
        {
            Dispose(false);
        }

        #endregion
    }
}
