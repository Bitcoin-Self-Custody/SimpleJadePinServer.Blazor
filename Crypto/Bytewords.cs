namespace SimpleJadePinServer.Blazor.Crypto;

// BC-UR Bytewords encoding: maps each byte to a 4-letter word from a fixed 256-word alphabet.
// Minimal encoding uses only first and last letter of each word (2 chars per byte).
// See: https://github.com/BlockchainCommons/Research/blob/master/papers/bcr-2020-012-bytewords.md
public static class Bytewords
{
    // The full 256-word alphabet — each word is exactly 4 lowercase letters.
    // Word index = byte value (0x00 = "able", 0xFF = "zoom").
    // NOTE: 'luau' at index 0x8b (139) — the task brief had a typo ('luab') which caused
    // a lookup collision with 'lamb' (0x7f). The correct BC-UR spec word is 'luau'.
    const string Words = "ableacidalsoapexaquaarchatomauntawayaxisbackbaldbarnbeltbetabiasbluebodybragbrewbulbbuzzcalmcashcatschefcityclawcodecolacookcostcruxcurlcuspcyandarkdatadaysdelidicedietdoordowndrawdropdrumdulldutyeacheasyechoedgeepicevenexamexiteyesfactfairfernfigsfilmfishfizzflapflewfluxfoxyfreefrogfuelfundgalagamegeargemsgiftgirlglowgoodgraygrimgurugushgyrohalfhanghardhawkheathelphighhillholyhopehornhutsicedideaidleinchinkyintoirisironitemjadejazzjoinjoltjowljudojugsjumpjunkjurykeepkenokeptkeyskickkilnkingkitekiwiknoblamblavalazyleaflegsliarlimplionlistlogoloudloveluaulucklungmainmanymathmazememomenumeowmildmintmissmonknailnavyneednewsnextnoonnotenumbobeyoboeomitonyxopenovalowlspaidpartpeckplaypluspoempoolposepuffpumapurrquadquizraceramprealredorichroadrockroofrubyruinrunsrustsafesagascarsetssilkskewslotsoapsolosongstubsurfswantacotasktaxitenttiedtimetinytoiltombtoystriptunatwinuglyundouniturgeuservastveryvetovialvibeviewvisavoidvowswallwandwarmwaspwavewaxywebswhatwhenwhizwolfworkyankyawnyellyogayurtzapszerozestzinczonezoom";

    // Lookup table: index = (lastChar * 26 + firstChar), value = byte value (-1 if invalid).
    // Allows O(1) decoding of 2-char minimal byteword pairs.
    static readonly int[] LookupTable = BuildLookupTable();

    static int[] BuildLookupTable()
    {
        var table = new int[26 * 26];
        Array.Fill(table, -1);
        for (var i = 0; i < 256; i++)
        {
            // Extract first and last characters of each 4-letter word to build the minimal encoding key.
            var firstChar = Words[i * 4] - 'a';
            var lastChar = Words[i * 4 + 3] - 'a';
            table[lastChar * 26 + firstChar] = i;
        }
        return table;
    }

    // Encodes bytes to minimal bytewords: each byte becomes 2 chars (first + last letter of its word).
    public static string EncodeMinimal(ReadOnlySpan<byte> data)
    {
        var chars = new char[data.Length * 2];
        for (var i = 0; i < data.Length; i++)
        {
            // Each word starts at offset = byteValue * 4 in the Words string.
            var wordIndex = data[i] * 4;
            chars[i * 2] = Words[wordIndex];
            chars[i * 2 + 1] = Words[wordIndex + 3];
        }
        return new string(chars);
    }

    // Decodes minimal bytewords back to bytes: each 2-char pair maps back to its byte value.
    // Throws ArgumentException on invalid pairs.
    public static byte[] DecodeMinimal(string encoded)
    {
        var result = new byte[encoded.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            var first = char.ToLower(encoded[i * 2]) - 'a';
            var last = char.ToLower(encoded[i * 2 + 1]) - 'a';
            var value = LookupTable[last * 26 + first];
            if (value < 0)
                throw new ArgumentException($"Invalid byteword pair: {encoded[i * 2]}{encoded[i * 2 + 1]}");
            result[i] = (byte)value;
        }
        return result;
    }
}
