^FX Receiving label, 4 x 3 inches (812 x 609 dots at 203 dpi).
^FX Shows the label put on goods at the receiving dock: a black header with reversed text, the purchase order number as
^FX a Code 93 symbol, and the supplier, delivery details and dock.
^FX Sample label from LabelScope. All data is made up.
^XA
^PW812
^LL609
^LH0,0
^FX Outer frame.
^FO4,4^GB804,601,4^FS
^FX Header band with reversed text.
^FO16,16^GB780,76,76,B^FS
^FO36,24^FR^A0N,60,60^FDRECEIVING^FS
^FO520,42^FR^A0N,36,36^FD2026-10-25^FS
^FX Purchase order number as a Code 93 symbol: 9 modules for each character plus the two check characters, 4 dots per module.
^FO16,104^GB780,240,3^FS
^FO36,114^A0N,24,24^FDPURCHASE ORDER^FS
^BY4,3,110
^FO190,152^BAN,110,Y,N,N^FDPO-48213^FS
^FX Supplier and delivery.
^FO16,356^GB520,240,3^FS
^FO36,366^A0N,24,24^FDSUPPLIER^FS
^FO36,396^A0N,46,46^FDExample Components Co.^FS
^FO36,452^A0N,24,24^FDDELIVERED BY^FS
^FO36,480^A0N,38,38^FDFreight carrier^FS
^FO36,530^A0N,24,24^FDRECEIVED BY^FS
^FO36,554^A0N,30,30^FDJ. Example^FS
^FO536,356^GB260,240,3^FS
^FO552,366^A0N,24,24^FDPIECES^FS
^FO552,396^A0N,90,90^FD6 / 6^FS
^FO552,500^A0N,24,24^FDDOCK DOOR^FS
^FO552,528^A0N,60,60^FDD-03^FS
^XZ
