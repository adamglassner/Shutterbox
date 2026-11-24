Test pictures for People and "search by what's in the photo". `expected.json` is what `tools/vision_ref.py expect` finds
in them; the Windows self-test and the Android on-device test check the apps find the same (faces in the same place,
embeddings with cosine >= 0.9 to these, the same top label).

All from scikit-image's sample data (public domain / CC0): `astronaut.jpg` (Eileen Collins, NASA), `astronaut2.jpg`
(the same, mirrored, cropped, scaled and darkened: the same person, a different picture), `cameraman.jpg`,
`cat.jpg` (Chelsea, by Stéfan van der Walt), `coffee.jpg` (by Rachel Michetti).
