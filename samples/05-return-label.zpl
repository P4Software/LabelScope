^FX Return label, 4 x 6 inches (812 x 1218 dots at 203 dpi).
^FX Shows the return address, the return authorization (RMA) number as a Code 128 symbol and a QR code that opens the
^FX return page on example.com, with plain instructions for the customer.
^FX Sample label from LabelScope. All data is made up.
^XA
^PW812
^LL1218
^LH0,0
^FX Outer frame.
^FO8,8^GB796,1202,4^FS
^FX Header band.
^FO20,20^GB772,100,100,B^FS
^FO44,34^FR^A0N,72,72^FDRETURN^FS
^FO470,62^FR^A0N,40,40^FDPREPAID LABEL^FS
^FX Return-to address.
^FO20,132^GB772,260,3^FS
^FO40,146^A0N,26,26^FDRETURN TO^FS
^FO40,178^A0N,56,56^FDExample Supply Co.^FS
^FO40,244^A0N,44,44^FDReturns Department^FS
^FO40,292^A0N,44,44^FD100 Sample Street, Dock 6^FS
^FO40,340^A0N,44,44^FDSpringfield, ST 12345^FS
^FX Return authorization number.
^FO20,404^GB772,246,3^FS
^FO40,416^A0N,26,26^FDRETURN AUTHORIZATION (RMA)^FS
^BY4,3,130
^FO60,456^BCN,130,Y,N,N^FDRMA-30517^FS
^FX QR code with the return page, and plain instructions beside it.
^FO20,662^GB772,330,3^FS
^FO40,676^A0N,26,26^FDSCAN TO TRACK YOUR RETURN^FS
^BQN,2,7,Q
^FO52,716^FDQA,https://example.com/returns/RMA-30517^FS
^FO400,716^A0N,38,38^FB370,6,6,L,0^FD1. Pack the items and seal the box.\&2. Stick this label on the box.\&3. Hand the box in at any drop-off point.^FS
^FX From the customer.
^FO20,1004^GB772,200,3^FS
^FO40,1016^A0N,26,26^FDFROM^FS
^FO40,1048^A0N,40,40^FDJ. Example^FS
^FO40,1094^A0N,36,36^FD200 Sample Avenue, Springfield, ST 54321^FS
^FO40,1140^A0N,30,30^FDOrder ORD-100417    Reason: wrong size^FS
^XZ
