namespace tot_gui_lib
{
    public interface ITinyRecipient<in TMessage> where TMessage : class, ITinyMessage
    {
        void Receive(TMessage message);
    }
}