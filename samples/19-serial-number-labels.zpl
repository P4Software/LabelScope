^FX Serial-number labels, 2 x 1 inch (406 x 203 dots at 203 dpi).
^FX One job prints five labels. The serial number command makes the number count up by one on each label, keeping
^FX its leading zeros, and a second counted field numbers the units 1 to 5. The serial is a Code 128 bar code.
^FX Sample label from LabelScope. All data is made up.
^XA
^PW406
^LL203
^LH0,0
^PQ5
^FX Product name and caption.
^FO12,8^A0N,28,28^FDEX-200 controller^FS
^FO12,40^A0N,20,20^FDSERIAL NUMBER^FS
^FX Serial number as a Code 128 bar code, starting at SN000101 and counting up by 1.
^BY2,3,64
^FO80,64^BCN,64,Y,N,N^SNSN000101,1,Y^FS
^FX Unit counter: UNIT 1 OF 5, UNIT 2 OF 5 and so on.
^FO12,168^A0N,24,24^FDUNIT^FS
^FO70,168^A0N,24,24^SN1,1,N^FS
^FO92,168^A0N,24,24^FDOF 5^FS
^FO216,172^A0N,20,20^FDExample Supply Co.^FS
^XZ
