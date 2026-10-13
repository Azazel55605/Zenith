Inter by Rasmus Andersson (https://rsms.me/inter), licensed under the SIL Open Font License 1.1
(https://openfontlicense.org). These files are static Regular (wght 400) and SemiBold (wght 600)
instances of InterVariable, subset to Basic Latin + Latin-1 + a few punctuation marks, hinting removed.
They are delivered in the independently versioned zenith-fonts package. The kernel
retains embedded recovery copies in src/Gui/Graphics/EmbeddedFonts.g.cs. Signed,
owned installed fonts take precedence on the next desktop initialization.

Hack (https://sourcefoundry.org/hack/) by Source Foundry, MIT License with the Bitstream Vera
License for the glyphs it derives from. Subset to Latin-1, a few punctuation and box-drawing
characters, hinting removed. Used by the terminal.

Regenerate everything with tools/gen-fonts.py.

Redistribution notices: LICENSE-Inter.txt and LICENSE-Hack.txt are included in the
font package. Build the payload with tools/build-font-package.py PAYLOAD_ROOT.
