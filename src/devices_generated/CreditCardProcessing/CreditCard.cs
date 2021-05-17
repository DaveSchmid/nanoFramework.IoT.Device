// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using Iot.Device.Common;
using Microsoft.Extensions.Logging;

namespace Iot.Device.Card.CreditCardProcessing
{
    /// <summary>
    /// The Credit Card class
    /// </summary>
    public class CreditCard
    {
        // APDU commands are using 5 elements in this order
        // Then the data are added
        private const byte Cla = 0x00;
        private const byte Ins = 0x01;
        private const byte P1 = 0x02;
        private const byte P2 = 0x03;
        private const byte Lc = 0x04;

        private const int MaxBufferSize = 260;

        // This is a string "1PAY.SYS.DDF01" (PPSE) to select the root directory
        // This is usually represented as { 0x31, 0x50, 0x41, 0x59, 0x2e, 0x53, 0x59, 0x53, 0x2e, 0x44, 0x44, 0x46, 0x30, 0x31 }
        private static readonly byte[] RootDirectory1 = Encoding.ASCII.GetBytes("1PAY.SYS.DDF01");
        // This is a string "2PAY.SYS.DDF01" (PPSE) to select the root directory
        // this is usually represented  as { 0x32, 0x50, 0x41, 0x59, 0x2e, 0x53, 0x59, 0x53, 0x2e, 0x44, 0x44, 0x46, 0x30, 0x31 }
        private static readonly byte[] RootDirectory2 = Encoding.ASCII.GetBytes("2PAY.SYS.DDF01");

        private CardTransceiver _nfc;
        private bool _alreadyReadSfi = false;
        private byte _target;
        private ILogger _logger;

        /// <summary>
        /// The size of the tailer elements. Some readers add an extra byte
        /// usually 0x00 especially NFC ones. While Smart Card readers usually do not
        /// </summary>
        public int TailerSize { get; set; }

        /// <summary>
        /// A list of Tags that is contained by the Credit Card
        /// </summary>
        public List<Tag> Tags { get; internal set; }

        /// <summary>
        /// The list of log entries in binary format
        /// </summary>
        public List<byte[]> LogEntries { get; internal set; }

        /// <summary>
        /// Create a Credit Card class
        /// </summary>
        /// <param name="nfc">A compatible Card reader</param>
        /// <param name="target">The target number as some readers needs it</param>
        /// <param name="tailerSize">Size of the tailer, most NFC readers add an extra byte 0x00</param>
        /// <remarks>The target number can be found with the NFC/Card reader you are using. For example the PN532 require a target number,
        /// the normal smart card readers usually don't as they only support 1 card at a time.</remarks>
        public CreditCard(CardTransceiver nfc, byte target, int tailerSize = 3)
        {
            _nfc = nfc;
            _target = target;
            Tags = new List<Tag>();
            LogEntries = new List<byte[]>();
            TailerSize = tailerSize;
            _logger = this.GetCurrentClassLogger();
        }

        /// <summary>
        /// Process external authentication
        /// </summary>
        /// <param name="issuerAuthenticationData">The authentication data</param>
        /// <returns>The error status</returns>
        public ErrorType ProcessExternalAuthentication(SpanByte issuerAuthenticationData)
        {
            if ((issuerAuthenticationData.Length < 8) || (issuerAuthenticationData.Length > 16))
            {
                throw new ArgumentException(nameof(issuerAuthenticationData), "Data needs to be more than 8 and less than 16 bytes length");
            }

            SpanByte toSend = new byte[5 + issuerAuthenticationData.Length];
            ApduCommands.ExternalAuthenticate.CopyTo(toSend);
            toSend[P1] = 0x00;
            toSend[P2] = 0x00;
            toSend[Lc] = (byte)issuerAuthenticationData.Length;
            issuerAuthenticationData.CopyTo(toSend.Slice(Lc));
            SpanByte received = new byte[MaxBufferSize];
            return RunSimpleCommand(toSend);
        }

        private ErrorType RunSimpleCommand(SpanByte toSend)
        {
            SpanByte received = new byte[MaxBufferSize];
            var ret = ReadFromCard(_target, toSend, received);
            if (ret >= TailerSize)
            {
                return new ProcessError(received.Slice(0, TailerSize)).ErrorType;
            }

            return ErrorType.Unknown;
        }

        /// <summary>
        /// Get a challenge to process authentication
        /// </summary>
        /// <param name="unpredictableNumber">the unpredictable number to be generated by the card, it should be 8 bytes</param>
        /// <returns>The error status</returns>
        public ErrorType GetChallenge(SpanByte unpredictableNumber)
        {
            if (unpredictableNumber.Length < 8)
            {
                throw new ArgumentException(nameof(unpredictableNumber), "Data has to be at least 8 bytes long.");
            }

            SpanByte toSend = new byte[5];
            ApduCommands.GetChallenge.CopyTo(toSend);
            toSend[P1] = 0x00;
            toSend[P2] = 0x00;
            toSend[P2 + 1] = 0x00;
            var ret = ReadFromCard(_target, toSend, unpredictableNumber);
            if (ret >= TailerSize)
            {
                return new ProcessError(unpredictableNumber.Slice(0, TailerSize)).ErrorType;
            }

            return ErrorType.Unknown;
        }

        /// <summary>
        /// Verify the pin. Note this command may not be supported for your specific credit card
        /// </summary>
        /// <param name="pindigits">The pin in a byte array, between 4 and 8 array length. Pin numbers should be bytes like in the following example:
        /// byte[] pin = new byte[] { 1, 2, 3, 4 };
        /// </param>
        /// <returns>The error status</returns>
        public ErrorType VerifyPin(ReadOnlySpanByte pindigits)
        {
            // Pin can only be 4 to C length
            if ((pindigits.Length < 0x04) && (pindigits.Length > 0x0C))
            {
                throw new ArgumentException(nameof(pindigits), "Data can only be between 4 and 12 digits");
            }

            // Encode the pin
            // The plain text offline PIN block shall be formatted as follows:
            // C N P P P P P/F P/F P/F P/F P/F P/F P/F P/F F F
            // where:
            //     |   Name        |   Value
            // C   | Control field | 4 bit binary number with value of 0010 (Hex '2')
            // N   | PIN length    | 4 bit binary number with permissible values of 0100 to 1100 (Hex '4' to 'C')
            // P   | PIN digit     | 4 bit binary number with permissible values of 0000 to 1001 (Hex '0' to '9')
            // P/F | PIN/filler    | Determined by PIN length
            // F   | Filler        | 4 bit binary number with a value of 1111 (Hex 'F')
            byte[] encodedPin = new byte[2 + (pindigits.Length + 1) / 2];
            encodedPin[0] = (byte)(0b0010_0000 + pindigits.Length);
            int index = 1;
            for (int i = 0; i < pindigits.Length; i += 2)
            {
                encodedPin[index] = (byte)(pindigits[i] << 4);
                if (i < (pindigits.Length - 1))
                {
                    encodedPin[index] += pindigits[i + 1];
                }
                else
                {
                    encodedPin[index] += 0x0F;
                }

                index++;
            }

            encodedPin[index] = 0xFF;

            SpanByte toSend = new byte[5 + encodedPin.Length];
            ApduCommands.Verify.CopyTo(toSend);
            toSend[P1] = 0x00;
            // We do support only plain text
            toSend[P2] = 0b1000_0000;
            toSend[Lc] = (byte)encodedPin.Length;
            encodedPin.CopyTo(toSend.Slice(Lc + 1));
            return RunSimpleCommand(toSend);
        }

        /// <summary>
        /// Get the number of pin tries left. Your credit card may not support this command.
        /// Use GetData(DataType.PinTryCounter) instead if you get a -1 as answer
        /// </summary>
        /// <returns>the number of tries left or -1 if not successful</returns>
        public int GetPinTries()
        {
            int tryLeft = -1;
            SpanByte toSend = new byte[5];
            ApduCommands.Verify.CopyTo(toSend);
            SpanByte received = new byte[MaxBufferSize];
            var ret = ReadFromCard(_target, toSend, received);

            if (ret == TailerSize)
            {
                var err = new ProcessError(received.Slice(0, TailerSize));
                if ((err.ErrorType == ErrorType.StateNonVolatileMemoryChangedAuthenticationFailed) ||
                    (err.ErrorType == ErrorType.StateNonVolatileMemoryChanged))
                {
                    tryLeft = err.CorrectLegnthOrBytesAvailable;
                }
            }

            return tryLeft;
        }

        /// <summary>
        /// Select an application identifier
        /// </summary>
        /// <param name="toSelect">The application identifier</param>
        /// <returns>The error status</returns>
        public ErrorType Select(SpanByte toSelect)
        {
            SpanByte toSend = new byte[6 + toSelect.Length];
            ApduCommands.Select.CopyTo(toSend);
            toSend[P1] = 0x04;
            toSend[P2] = 0x00;
            toSend[Lc] = (byte)toSelect.Length;
            toSelect.CopyTo(toSend.Slice(Lc + 1));
            toSend[toSend.Length - 1] = 0x00;
            SpanByte received = new byte[MaxBufferSize];
            var ret = ReadFromCard(_target, toSend, received);
            if (ret >= TailerSize)
            {
                if (ret == TailerSize)
                {
                    // It's an error, process it
                    var err = new ProcessError(received.Slice(0, TailerSize));
                    return err.ErrorType;
                }

                FillTagList(Tags, received.Slice(0, ret - TailerSize));

                return ErrorType.ProcessCompletedNormal;
            }

            return ErrorType.Unknown;
        }

        private void FillTagList(List<Tag> tags, ReadOnlySpanByte span, uint parent = 0x00)
        {
            // We don't decode template 0x80
            if (span.Length == 0)
            {
                return;
            }

            var elem = new BerSplitter(span);
            foreach (var tag in elem.Tags)
            {
                // If it is a template or composed, then we need to split it
                if ((TagList.Tags.Where(m => m.TagNumber == tag.TagNumber).FirstOrDefault()?.IsTemplate == true) || tag.IsConstructed)
                {
                    if (tag.Tags is null)
                    {
                        tag.Tags = new List<Tag>();
                    }

                    FillTagList(tag.Tags, tag.Data, tag.TagNumber);
                }

                // Data Object Lists are special and not BER encoded, they have only the tag number encoded
                // Like for the traditional tags but the next element is a single byte indicating the size
                // Of the object
                if (TagList.Tags.Where(m => m.TagNumber == tag.TagNumber).FirstOrDefault()?.IsDol == true)
                {
                    if (tag.Tags is null)
                    {
                        tag.Tags = new List<Tag>();
                    }

                    DecodeDol(tag);
                }

                tag.Parent = parent;
                tags.Add(tag);
            }
        }

        private void DecodeDol(Tag tag)
        {
            int index = 0;
            while (index < tag.Data.Length)
            {
                // Decode mono dimension (so 1 byte array) Ber elements but which can have ushort or byte tags
                var dol = new Tag();
                dol.Data = new byte[1];
                if ((tag.Data[index] & 0b0001_1111) == 0b0001_1111)
                {
                    dol.TagNumber = BinaryPrimitives.ReadUInt16BigEndian(tag.Data.AsSpan().Slice(index, 2));
                    index += 2;
                    tag.Data.AsSpan().Slice(index++, 1).CopyTo(dol.Data);
                }
                else
                {
                    dol.TagNumber = tag.Data[index++];
                    tag.Data.AsSpan().Slice(index++, 1).CopyTo(dol.Data);
                }

                dol.Parent = tag.TagNumber;
                tag.Tags.Add(dol);
            }
        }

        /// <summary>
        /// Gather all the public information present in the credit card.
        /// Fill then Tag list with all the found information. You can get
        /// all the credit card information in the Tags property.
        /// </summary>
        public void ReadCreditCardInformation()
        {
            // This is a string "2PAY.SYS.DDF01" (PPSE) to select the root directory
            var ret = Select(RootDirectory2);
            // If not working, then try with "1PAY.SYS.DDF01"
            if (ret != ErrorType.ProcessCompletedNormal)
            {
                ret = Select(RootDirectory1);
            }

            if (ret == ErrorType.ProcessCompletedNormal)
            {
                if (!FillTags())
                {
                    _alreadyReadSfi = false;
                    FillTags();
                }
            }

            // Search for all tags with entries
            var entries = Tag.SearchTag(Tags, 0x9F4D).FirstOrDefault();
            if (entries is object)
            {
                // SFI entries is first byte and number of records is second one
                ReadLogEntries(entries.Data[0], entries.Data[1]);
            }
        }

        /// <summary>
        /// Please refer to EMV 4.3 Book 3, Integrated Circuit Card Specifications for Payment Systems.
        /// https://www.emvco.com/emv-technologies/contact/. The file system and how to access it is mainly
        /// explained on chapter 5 and chapter 7.
        /// </summary>
        /// <returns></returns>
        private bool FillTags()
        {
            // Find all Application Template = 0x61
            List<Tag> appTemplates = Tag.SearchTag(Tags, 0x61);
            if (appTemplates.Count > 0)
            {
                _logger.LogDebug($"Number of App Templates: {appTemplates.Count}");
                foreach (var app in appTemplates)
                {
                    // Find the Application Identifier 0x4F
                    Tag appIdentifier = Tag.SearchTag(app.Tags, 0x4F).First();
                    // Find the Priority Identifier 0x87
                    Tag? appPriotity = Tag.SearchTag(app.Tags, 0x87).FirstOrDefault();
                    // As it is not mandatory, some cards will have only 1
                    // application and this may not be present
                    if (appPriotity is null)
                    {
                        appPriotity = new Tag() { Data = new byte[1] { 0 } };
                    }

                    // do we have a PDOL tag 0x9F38
                    _logger.LogDebug($"APPID: {BitConverter.ToString(appIdentifier.Data)}, Priority: {appPriotity.Data[0]}");
                    var ret = Select(appIdentifier.Data);
                    if (ret == ErrorType.ProcessCompletedNormal)
                    {
                        // We need to select the Template 0x6F where the Tag 0x84 contains the same App Id and where we have a template A5 attached.
                        var templates = Tags
                            .Where(m => m.TagNumber == 0x6F)
                            .Where(m => m.Tags.Where(t => t.TagNumber == 0x84).Where(t => t.Data.SequenceEqual(appIdentifier.Data)) is object)
                            .Where(m => m.Tags.Where(t => t.TagNumber == 0xA5) is object);
                        // Only here we may find a PDOL tag 0X9F38
                        Tag? pdol = null;
                        foreach (var temp in templates)
                        {
                            // We are sure to have 0xA5, so select it and search for PDOL
                            pdol = Tag.SearchTag(temp.Tags, 0xA5).FirstOrDefault()?.Tags.Where(m => m.TagNumber == 0x9F38).FirstOrDefault();
                            if (pdol is object)
                            {
                                break;
                            }
                        }

                        SpanByte received = new byte[260];
                        byte sumDol = 0;
                        // Do we have a PDOL?
                        if (pdol is object)
                        {
                            // So we need to send as may bytes as it request
                            foreach (var dol in pdol.Tags)
                            {
                                sumDol += dol.Data[0];
                            }
                        }

                        // We send only 0 but the right number
                        SpanByte toSend = new byte[2 + sumDol];
                        // Template command, Tag 83
                        toSend[0] = 0x83;
                        toSend[1] = sumDol;
                        // If we have a PDOL, then we need to fill it properly
                        // Some fields are mandatory
                        int index = 2;
                        if (pdol is object)
                        {
                            foreach (var dol in pdol.Tags)
                            {
                                // TerminalTransactionQualifier
                                if (dol.TagNumber == 0x9F66)
                                {
                                    // Select modes to get a maximum of data
                                    TerminalTransactionQualifier ttq = TerminalTransactionQualifier.MagStripeSupported | TerminalTransactionQualifier.EmvModeSupported | TerminalTransactionQualifier.EmvContactChipSupported |
                                        TerminalTransactionQualifier.OnlinePinSupported | TerminalTransactionQualifier.SignatureSupported | TerminalTransactionQualifier.ContactChipOfflinePinSupported |
                                        TerminalTransactionQualifier.ConsumerDeviceCvmSupported | TerminalTransactionQualifier.IssuerUpdateProcessingSupported;
                                    // Encode the TTq
                                    BinaryPrimitives.TryWriteUInt32BigEndian(toSend.Slice(index, 4), (uint)ttq);
                                }

                                // Transaction amount
                                else if (dol.TagNumber == 0x9F02)
                                {
                                    // Ask authorization for the minimum, just to make sure
                                    // It's more than 0
                                    toSend[index + 5] = 1;
                                }

                                // 9F1A-Terminal Country Code,
                                else if (dol.TagNumber == 0x9F1A)
                                {
                                    // Let's say we are in France
                                    toSend[index] = 0x02;
                                    toSend[index + 1] = 0x50;
                                }

                                // 009A-Transaction Date
                                else if (dol.TagNumber == 0x9A)
                                {
                                    toSend[index] = NumberHelper.Dec2Bcd((DateTime.Now.Year % 100));
                                    toSend[index + 1] = NumberHelper.Dec2Bcd((DateTime.Now.Month));
                                    toSend[index + 2] = NumberHelper.Dec2Bcd((DateTime.Now.Day));
                                }

                                // 0x9F37 Unpredictable number
                                else if (dol.TagNumber == 0x9F37)
                                {
                                    var rand = new Random();
                                    rand.NextBytes(toSend.Slice(index, dol.Data[0]));
                                }

                                // Currency
                                else if (dol.TagNumber == 0x5F2A)
                                {
                                    // We will ask for Euro
                                    toSend[index] = 0x09;
                                    toSend[index + 1] = 0x78;
                                }

                                index += dol.Data[0];
                            }
                        }

                        // Ask for all the process options
                        ret = GetProcessingOptions(toSend, received);
                        Tag? appLocator = null;
                        if (ret == ErrorType.ProcessCompletedNormal)
                        {
                            // Check if we have an Application File Locator 0x94 in 0x77
                            var tg = Tag.SearchTag(Tags, 0x77);
                            if (tg.Count > 0)
                            {
                                appLocator = tg.Last().Tags.Where(t => t.TagNumber == 0x94).FirstOrDefault();
                            }
                        }

                        if ((ret == ErrorType.ProcessCompletedNormal) && (appLocator is object))
                        {
                            // Now decode the appLocator
                            // Format is SFI - start - stop - number of records
                            List<ApplicationDataDetail> details = new List<ApplicationDataDetail>();
                            for (int i = 0; i < appLocator.Data.Length / 4; i++)
                            {
                                ApplicationDataDetail detail = new ApplicationDataDetail()
                                {
                                    Sfi = (byte)(appLocator.Data[4 * i] >> 3),
                                    Start = appLocator.Data[4 * i + 1],
                                    End = appLocator.Data[4 * i + 2],
                                    NumberOfRecords = appLocator.Data[4 * i + 3],
                                };
                                details.Add(detail);
                            }

                            // Now get all the records
                            foreach (var detail in details)
                            {
                                for (byte record = detail.Start; record < detail.End + 1; record++)
                                {
                                    ret = ReadRecord(detail.Sfi, record);
                                    _logger.LogDebug($"Read record {record}, SFI {detail.Sfi}, status: {ret}");
                                }

                            }

                            _alreadyReadSfi = true;
                        }
                        else if (!_alreadyReadSfi)
                        {
                            // We go thru all the SFI and first 5 records
                            // According to the documentation, first 10 ones are supposed to
                            // contain the core information
                            for (byte record = 1; record < 5; record++)
                            {
                                // 1 fro 10 is for Application Elementary Files
                                for (byte sfi = 1; sfi < 11; sfi++)
                                {
                                    ret = ReadRecord(sfi, record);
                                    _logger.LogDebug($"Read record {record}, SFI {sfi}, status: {ret}");
                                }
                            }

                            _alreadyReadSfi = true;
                        }
                    }

                    // Get few additional data
                    GetData(DataType.ApplicationTransactionCounter);
                    GetData(DataType.LastOnlineAtcRegister);
                    GetData(DataType.LogFormat);
                    GetData(DataType.PinTryCounter);
                }

                return true;
            }
            else
            {
                // It's the old way, so looking for tag 0x88
                var appSfi = Tag.SearchTag(Tags, 0x88).FirstOrDefault();
                if (appSfi is object)
                {
                    _logger.LogDebug($"AppSFI: {appSfi.Data[0]}");
                    for (byte record = 1; record < 10; record++)
                    {
                        var ret = ReadRecord(appSfi.Data[0], record);
                        _logger.LogDebug($"Read record {record}, SFI {appSfi.Data[0]}, status: {ret}");
                    }

                    _alreadyReadSfi = true;
                }

                return false;
            }
        }

        /// <summary>
        /// Read log records
        /// </summary>
        public void ReadLogEntries(byte sfi, byte numberOfRecords)
        {
            for (byte record = 1; record < numberOfRecords + 1; record++)
            {
                var ret = ReadRecord(sfi, record, true);
                _logger.LogDebug($"Read record {record}, SFI {sfi},status: {ret}");
            }
        }

        /// <summary>
        /// Read a specific record
        /// </summary>
        /// <param name="sfi">The Short File Identifier</param>
        /// <param name="record">The Record to read</param>
        /// <param name="isLogEntry">Are we reading a log entry or something else?</param>
        /// <returns>The error status</returns>
        public ErrorType ReadRecord(byte sfi, byte record, bool isLogEntry = false)
        {
            if (sfi > 31)
            {
                return ErrorType.WrongParameterP1P2FunctionNotSupported;
            }

            SpanByte toSend = new byte[5];
            ApduCommands.ReadRecord.CopyTo(toSend);
            toSend[P1] = record;
            toSend[P2] = (byte)((sfi << 3) | (0b0000_0100));
            toSend[Lc] = 0x00;
            SpanByte received = new byte[MaxBufferSize];
            var ret = ReadFromCard(_target, toSend, received);
            if (ret >= TailerSize)
            {
                if (ret == TailerSize)
                {
                    // It's an error, process it
                    var err = new ProcessError(received.Slice(0, TailerSize));
                    if (err.ErrorType == ErrorType.WrongLength)
                    {
                        toSend[Lc] = err.CorrectLegnthOrBytesAvailable;
                        ret = ReadFromCard(_target, toSend, received);
                        if (ret == TailerSize)
                        {
                            return new ProcessError(received.Slice(0, TailerSize)).ErrorType;
                        }
                    }
                    else
                    {
                        return err.ErrorType;
                    }
                }

                if (isLogEntry)
                {
                    LogEntries.Add(received.Slice(0, ret - TailerSize).ToArray());
                }
                else
                {
                    FillTagList(Tags, received.Slice(0, ret - TailerSize));
                }

                return new ProcessError(received.Slice(ret - TailerSize)).ErrorType;
            }

            return ErrorType.Unknown;
        }

        /// <summary>
        /// Get Processing Options
        /// </summary>
        /// <param name="pdolToSend">The PDOL array to send</param>
        /// <param name="pdol">The return PDOL elements</param>
        /// <returns>The error status</returns>
        public ErrorType GetProcessingOptions(ReadOnlySpanByte pdolToSend, SpanByte pdol)
        {
            SpanByte toSend = new byte[6 + pdolToSend.Length];
            ApduCommands.GetProcessingOptions.CopyTo(toSend);
            toSend[P1] = 0x00;
            toSend[P2] = 0x00;
            toSend[Lc] = (byte)(pdolToSend.Length);
            pdolToSend.CopyTo(toSend.Slice(Lc + 1));
            toSend[Lc + pdolToSend.Length] = 0x00;
            SpanByte received = new byte[MaxBufferSize];
            var ret = ReadFromCard(_target, toSend, received);
            if (ret >= TailerSize)
            {
                if (ret == TailerSize)
                {
                    // It's an error, process it
                    return new ProcessError(received.Slice(0, TailerSize)).ErrorType;
                }

                FillTagList(Tags, received.Slice(0, ret - TailerSize));
                received.Slice(0, ret - TailerSize).CopyTo(pdol);
                return ErrorType.ProcessCompletedNormal;
            }

            return ErrorType.Unknown;
        }

        /// <summary>
        /// Get additional data
        /// </summary>
        /// <param name="dataType">The type of data to read</param>
        /// <returns>The error status</returns>
        public ErrorType GetData(DataType dataType)
        {
            SpanByte toSend = new byte[5];
            ApduCommands.GetData.CopyTo(toSend);
            switch (dataType)
            {
                case DataType.ApplicationTransactionCounter:
                    // 9F36
                    toSend[P1] = 0x9F;
                    toSend[P2] = 0x36;
                    break;
                case DataType.PinTryCounter:
                    // 9F17
                    toSend[P1] = 0x9F;
                    toSend[P2] = 0x17;
                    break;
                case DataType.LastOnlineAtcRegister:
                    // 9F13
                    toSend[P1] = 0x9F;
                    toSend[P2] = 0x13;
                    break;
                case DataType.LogFormat:
                    // 9F4F
                    toSend[P1] = 0x9F;
                    toSend[P2] = 0x4F;
                    break;
                default:
                    break;
            }

            toSend[Lc] = 0x00;
            SpanByte received = new byte[MaxBufferSize];
            var ret = ReadFromCard(_target, toSend, received);
            if (ret >= TailerSize)
            {
                if (ret == TailerSize)
                {
                    // It's an error, process it
                    var err = new ProcessError(received.Slice(0, TailerSize));
                    if (err.ErrorType == ErrorType.WrongLength)
                    {
                        toSend[Lc] = err.CorrectLegnthOrBytesAvailable;
                        ret = ReadFromCard(_target, toSend, received);
                        err = new ProcessError(received.Slice(ret - TailerSize));
                        if (err.ErrorType != ErrorType.ProcessCompletedNormal)
                        {
                            return err.ErrorType;
                        }
                    }
                }

                FillTagList(Tags, received.Slice(0, ret - TailerSize));
                _logger.LogDebug($"{BitConverter.ToString(received.Slice(0, ret).ToArray())}");
                var ber = new BerSplitter(received.Slice(0, ret - TailerSize));
                foreach (var b in ber.Tags)
                {
                    _logger.LogDebug($"DataType: {dataType}, Tag: {b.TagNumber.ToString("X4")}, Data: {BitConverter.ToString(b.Data)}");
                }

                return new ProcessError(received.Slice(ret - TailerSize)).ErrorType;
            }

            return ErrorType.Unknown;
        }

        private int ReadFromCard(byte target, ReadOnlySpanByte toSend, SpanByte received)
        {
            var ret = _nfc.Transceive(_target, toSend, received);
            if (ret >= TailerSize)
            {
                if (ret == TailerSize)
                {
                    // It's an error, process it
                    var err = new ProcessError(received.Slice(0, TailerSize));
                    if (err.ErrorType == ErrorType.BytesStillAvailable)
                    {
                        // Read the rest of the bytes
                        SpanByte toGet = new byte[5];
                        ApduCommands.GetBytesToRead.CopyTo(toGet);
                        toGet[4] = err.CorrectLegnthOrBytesAvailable;
                        ret = _nfc.Transceive(_target, toGet, received);
                    }
                }
            }

            return ret;
        }
    }
}
