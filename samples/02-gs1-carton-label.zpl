^FX GS1 logistics carton label, 4 x 6 inches (812 x 1218 dots at 203 dpi).
^FX Laid out in the three sections of a GS1 logistics label: ship from and ship to, the content (a GS1-128 symbol
^FX with the contained item GTIN and the item count), and the SSCC that identifies the carton.
^FX Sample label from LabelScope. All data is made up.
^XA
^PW812
^LL1218
^LH0,0
^FX Outer frame.
^FO8,8^GB796,1202,4^FS
^FX Section A: ship from and ship to.
^FO20,20^GB380,230,3^FS
^FO20,20^GB380,40,40,B^FS
^FO32,28^FR^A0N,26,26^FDSHIP FROM^FS
^FO36,72^A0N,34,34^FDExample Supply Co.^FS
^FO36,114^A0N,30,30^FDDock 2^FS
^FO36,150^A0N,30,30^FD100 Sample Street^FS
^FO36,186^A0N,30,30^FDSpringfield, ST 12345^FS
^FO412,20^GB380,230,3^FS
^FO412,20^GB380,40,40,B^FS
^FO424,28^FR^A0N,26,26^FDSHIP TO^FS
^FO428,72^A0N,34,34^FDSample Retail Store 12^FS
^FO428,114^A0N,30,30^FDReceiving Dock 4^FS
^FO428,150^A0N,30,30^FD200 Sample Avenue^FS
^FO428,186^A0N,30,30^FDSpringfield, ST 54321^FS
^FX Section B: content. The GTIN is that of the items inside the carton, the count says how many.
^FO20,262^GB772,370,3^FS
^FO20,262^GB772,40,40,B^FS
^FO32,270^FR^A0N,26,26^FDCONTENT^FS
^FO36,316^A0N,40,40^FDCeramic mug, 350 ml^FS
^FO36,364^A0N,30,30^FDItem GTIN 10614141000019^FS
^FO560,316^A0N,30,30^FDCOUNT^FS
^FO560,346^A0N,64,64^FD24^FS
^BY4,3,120
^FO72,420^BCN,120,Y,N,N,D^FD(02)10614141000019(37)24^FS
^FX Section C: the SSCC, the serial number that identifies this one carton.
^FO20,644^GB772,430,3^FS
^FO20,644^GB772,40,40,B^FS
^FO32,652^FR^A0N,26,26^FDSSCC (SERIAL SHIPPING CONTAINER CODE)^FS
^BY4,3,250
^FO72,720^BCN,250,Y,N,N,D^FD(00)306141410000123453^FS
^FX Footer line.
^FO36,1090^A0N,34,34^FDCarton 1 of 40^FS
^FO420,1090^A0N,34,34^FDOrder ORD-100417^FS
^FO36,1138^A0N,34,34^FDGross weight 11.6 kg^FS
^FO420,1138^A0N,34,34^FDPacked 2026-10-25^FS
^XZ
