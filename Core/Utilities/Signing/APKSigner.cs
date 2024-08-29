﻿using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.X509.Store;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Asn1.Cms;
using System.IO;
using System;
using System.Linq;
using System.Collections.Generic;
using Ionic.Zip;

namespace MelonLoader.Installer.Core.Utilities.Signing
{
    public class APKSigner
    {
        private X509Certificate _xCert;
        private AsymmetricKeyParameter _privateKey;
        private readonly SHA256 _sha;

        private readonly UTF8Encoding _encoding;
        private readonly IPatchLogger _logger;

        public APKSigner(string pemData, IPatchLogger patchLogger)
        {
            _encoding = new UTF8Encoding(false);
            _sha = SHA256.Create();
            _logger = patchLogger;

            LoadCerts(pemData);
        }

        private void LoadCerts(string pemData)
        {
            _logger.Log("Reading certificates");

            using (var reader = new StringReader(pemData))
            {
                // Iterate through the PEM objects until we find the public or private key
                var pemReader = new PemReader(reader);
                object pemObject;
                while ((pemObject = pemReader.ReadObject()) != null)
                {
                    _xCert ??= pemObject as X509Certificate;
                    _privateKey ??= (pemObject as AsymmetricCipherKeyPair)?.Private;
                }
            }

            if (_xCert == null)
                throw new System.Security.SecurityException("Certificate could not be loaded from PEM data.");

            if (_privateKey == null)
                throw new System.Security.SecurityException("Private Key could not be loaded from PEM data.");
        }

        public void Sign(string apkPath)
        {
            /* This isn't necessary and should make it so on Quest it won't keep screaming at the user that they should restore the app, until they patch it at least.
            
            _logger.Log("Signing with V1, this can take a few");
            SignV1(apkPath);*/

            _logger.Log("Aligning");
            APKAligner.AlignApk(apkPath);

            _logger.Log("Signing with V2");
            SignV2(apkPath);

            _logger.Log("Done");
        }

        #region V1

        private void SignV1(string apkPath)
        {
            using ZipFile apkArchive = new(apkPath);

            #region Create MANIFEST.MF

            using MemoryStream manifestStream = new();
            using StreamWriter manifestWriter = AsStreamWriter(manifestStream);

            using MemoryStream sigHolderStream = new();
            using StreamWriter sigHolderWriter = AsStreamWriter(sigHolderStream);

            manifestWriter.WriteLine("Manifest-Version: 1.0");
            manifestWriter.WriteLine("Created-By: LemonLoader");
            manifestWriter.WriteLine();

            manifestWriter.Close();

            foreach (ZipEntry entry in apkArchive.Entries)
            {
                if (entry.FileName.StartsWith("META-INF"))
                    continue;

                WriteDigests(entry, manifestStream, sigHolderWriter);
            }

            sigHolderWriter.Close();

            #endregion

            #region Create LEMON.SF

            using MemoryStream sigStream = new();
            using StreamWriter sigWriter = AsStreamWriter(sigStream);

            manifestStream.Seek(0, SeekOrigin.Begin);
            byte[] manifestSha = _sha.ComputeHash(manifestStream);

            sigWriter.WriteLine("Signature-Version: 1.0");
            sigWriter.WriteLine("Created-By: LemonLoader");
            sigWriter.WriteLine($"SHA-256-Digest-Manifest: {Convert.ToBase64String(manifestSha)}");
            sigWriter.WriteLine();

            sigWriter.Close();

            sigHolderStream.Seek(0, SeekOrigin.Begin);
            sigHolderStream.CopyTo(sigStream);

            #endregion

            #region Add to APK

            // Remove old META-INF files
            apkArchive.RemoveEntries(apkArchive.Entries.Where(a => a.FileName.StartsWith("META-INF")).ToList());

            // Add the new
            manifestStream.Seek(0, SeekOrigin.Begin);
            sigStream.Seek(0, SeekOrigin.Begin);

            ZipEntry manifestEntry = apkArchive.AddFile("META-INF/MANIFEST.MF");
            ZipEntry sigEntry = apkArchive.AddFile("META-INF/LEMON.SF");
            ZipEntry rsaEntry = apkArchive.AddFile("META-INF/LEMON.RSA");

            apkArchive.UpdateEntry(manifestEntry.FileName, manifestStream);
            apkArchive.UpdateEntry(sigEntry.FileName, sigStream);

            sigStream.Seek(0, SeekOrigin.Begin);

            byte[] signedSig = GetSignatureFileSig(sigStream.ToArray());
            apkArchive.UpdateEntry(rsaEntry.FileName, signedSig);

            #endregion

            apkArchive.Save();
        }

        private void WriteDigests(ZipEntry entry, Stream manifestStream, StreamWriter sigHolderWriter)
        {
            using Stream entryStream = entry.OpenReader();
            string entryDigest = Convert.ToBase64String(_sha.ComputeHash(entryStream));

            using MemoryStream proxyStream = new();
            using StreamWriter proxyWriter = AsStreamWriter(proxyStream);

            proxyWriter.WriteLine($"Name: {entry.FileName}");
            proxyWriter.WriteLine($"SHA-256-Digest: {entryDigest}");
            proxyWriter.WriteLine();

            proxyWriter.Close();

            proxyStream.Seek(0, SeekOrigin.Begin);

            sigHolderWriter.WriteLine($"Name: {entry.FileName}");
            sigHolderWriter.WriteLine($"SHA-256-Digest: {Convert.ToBase64String(_sha.ComputeHash(proxyStream))}");
            sigHolderWriter.WriteLine();
            proxyStream.Seek(0, SeekOrigin.Begin);
            proxyStream.CopyTo(manifestStream);
        }

        private byte[] GetSignatureFileSig(byte[] sfBytes)
        {
            var certStore = X509StoreFactory.Create("Certificate/Collection", new X509CollectionStoreParameters(new List<X509Certificate> { _xCert }));
            CmsSignedDataGenerator dataGen = new();
            dataGen.AddCertificates(certStore);
            dataGen.AddSigner(_privateKey, _xCert, CmsSignedGenerator.EncryptionRsa, CmsSignedGenerator.DigestSha256);

            // Content is detached - i.e. not included in the signature block itself
            CmsProcessableByteArray detachedContent = new(sfBytes);
            var signedContent = dataGen.Generate(detachedContent, false);

            // Get the signature in the proper ASN.1 structure for java to parse it properly.  Lots of trial and error
            var signerInfos = signedContent.GetSignerInfos();
            var signer = signerInfos.GetSigners().Cast<SignerInformation>().First();
            SignerInfo signerInfo = signer.ToSignerInfo();
            Asn1EncodableVector digestAlgorithmsVector =
            [
                new AlgorithmIdentifier(new DerObjectIdentifier("2.16.840.1.101.3.4.2.1"), DerNull.Instance)
            ];
            ContentInfo encapContentInfo = new(new DerObjectIdentifier("1.2.840.113549.1.7.1"), null);
            Asn1EncodableVector asnVector =
            [
                X509CertificateStructure.GetInstance(Asn1Object.FromByteArray(_xCert.GetEncoded()))
            ];
            Asn1EncodableVector signersVector = [signerInfo.ToAsn1Object()];
            SignedData signedData = new(new DerSet(digestAlgorithmsVector), encapContentInfo, new BerSet(asnVector), null, new DerSet(signersVector));
            ContentInfo contentInfo = new(new DerObjectIdentifier("1.2.840.113549.1.7.2"), signedData);
            return contentInfo.GetDerEncoded();
        }

        private StreamWriter AsStreamWriter(MemoryStream ms) => new(ms, _encoding, 1024, true);

        #endregion

        #region V2

        private void SignV2(string path)
        {
            FileStream fs = new(path, FileMode.Open);
            using FileMemory memory = new(fs);
            using MemoryStream ms = new();
            using FileMemory outMemory = new(ms);
            memory.Position = memory.Length() - 22;
            while (memory.ReadInt() != EndOfCentralDirectory.SIGNATURE)
            {
                memory.Position -= 4 + 1;
            }
            memory.Position -= 4;
            var eocdPosition = memory.Position;
            EndOfCentralDirectory eocd = new(memory);
            if (eocd == null)
                return;
            var cd = eocd.OffsetOfCD;
            memory.Position = cd - 16 - 8;
            var d = memory.ReadULong();
            var d2 = memory.ReadString(16);
            var section1 = GetSectionDigests(fs, 0, cd);
            var section3 = GetSectionDigests(fs, cd, eocdPosition);
            var section4 = GetSectionDigests(fs, eocdPosition, fs.Length);

            var digestChunks = section1.Concat(section3).Concat(section4).ToList();

            byte[] bytes = new byte[1 + 4];
            bytes[0] = 0x5a;
            byte[] sizeBytes = BitConverter.GetBytes((uint)digestChunks.Count);
            bytes[1] = sizeBytes[0];
            bytes[2] = sizeBytes[1];
            bytes[3] = sizeBytes[2];
            bytes[4] = sizeBytes[3];
            var digest = _sha.ComputeHash([.. bytes, .. digestChunks.Aggregate((a, b) => a.Concat(b).ToArray())]);

            uint algorithm = 0x0103;

            APKSignatureSchemeV2 block = new();
            APKSignatureSchemeV2.Signer signer = new();

            using MemoryStream signedDataMs = new();
            using FileMemory memorySignedData = new(signedDataMs);
            var signedData = new APKSignatureSchemeV2.Signer.BlockSignedData();
            signedData.Digests.Add(new APKSignatureSchemeV2.Signer.BlockSignedData.Digest(algorithm, digest));

            signedData.Certificates.Add(_xCert.GetEncoded());

            signedData.Write(memorySignedData);
            signer.SignedData = signedDataMs.ToArray();

            ISigner signerType = SignerUtilities.GetSigner("SHA256WithRSA");
            signerType.Init(true, _privateKey);
            signerType.BlockUpdate(signer.SignedData, 0, signer.SignedData.Length);

            signer.Signatures.Add(new APKSignatureSchemeV2.Signer.BlockSignature(algorithm, signerType.GenerateSignature()));
            signer.PublicKey = _xCert.CertificateStructure.SubjectPublicKeyInfo.GetDerEncoded();
            block.Signers.Add(signer);

            APKSigningBlock signingBlock = new();
            signingBlock.Values.Add(block.ToIDValuePair());

            fs.Position = 0;
            outMemory.WriteBytes(memory.ReadBytes(cd));
            signingBlock.Write(outMemory);
            eocd.OffsetOfCD = (int)ms.Position;
            outMemory.WriteBytes(memory.ReadBytes((int)(eocdPosition - cd)));
            eocd.Write(outMemory);

            fs.SetLength(0);
            ms.Position = 0;
            ms.CopyTo(fs);
            fs.Close();
        }

        private List<byte[]> GetSectionDigests(FileStream fs, long startOffset, long endOffset)
        {
            var digests = new List<byte[]>();
            int chunkSize = 1024 * 1024;
            for (long i = startOffset; i < endOffset; i += chunkSize)
            {
                fs.Position = i;
                var size = Math.Min(endOffset - i, chunkSize);
                byte[] bytes = new byte[1 + 4 + size];
                bytes[0] = 0xa5;
                byte[] sizeBytes = BitConverter.GetBytes((uint)size);
                bytes[1] = sizeBytes[0];
                bytes[2] = sizeBytes[1];
                bytes[3] = sizeBytes[2];
                bytes[4] = sizeBytes[3];
                fs.Read(bytes, 5, (int)size);
                digests.Add(_sha.ComputeHash(bytes));
            }
            return digests;
        }

        #endregion
    }
}
