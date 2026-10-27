/* Compiler-emitted structure initialization/copy helpers for freestanding clients. */
void *memset(void *destination,int value,unsigned long size){
    unsigned char *p=destination;for(unsigned long i=0;i<size;i++)p[i]=(unsigned char)value;return destination;
}
void *memcpy(void *destination,const void *source,unsigned long size){
    unsigned char *d=destination;const unsigned char *s=source;
    for(unsigned long i=0;i<size;i++)d[i]=s[i];
    return destination;
}
